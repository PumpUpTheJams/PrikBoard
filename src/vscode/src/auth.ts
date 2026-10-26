import * as vscode from 'vscode'
import { api, configureClient, errorMessage, setUnauthorizedHandler } from '../../web/src/api/client'
import type { Caller } from '../../web/src/api/types'
import { signInWithPrikBoard } from './connect'
import { refreshTokens, signInWithBrowser, type OidcConfig, type TokenSet } from './oidc'

const CREDENTIAL_KEY = 'prikboard.credential'
/** Where the first version kept a pasted access token. */
const LEGACY_TOKEN_KEY = 'prikboard.accessToken'
/** Refresh this long before the access token runs out. */
const REFRESH_MARGIN_MS = 60_000

export function setting<T>(name: string, fallback: T): T {
  return vscode.workspace.getConfiguration('prikboard').get<T>(name, fallback)
}

/** The API origin without a trailing slash. */
export const serverUrl = () => setting('serverUrl', 'http://localhost:5080').replace(/\/+$/, '')

/** Where the web app runs; defaults to the API origin, which serves it in a deployment. */
export const webUrl = () => (setting('webUrl', '') || serverUrl()).replace(/\/+$/, '')

/**
 * Whether someone chose a server, rather than the extension falling back to the local development default. Only the
 * user settings count: the address settings are application-scoped, so a repository's workspace settings can't pick
 * where the credential goes.
 */
function serverChosen() {
  const inspected = vscode.workspace.getConfiguration('prikboard').inspect<string>('serverUrl')
  return !!inspected?.globalValue?.trim()
}

/** Credentials go to this address, so it must be encrypted unless it is this machine. */
function checkAddress(value: string) {
  let url: URL
  try {
    url = new URL(value.trim())
  } catch {
    return 'Enter the address you open PrikBoard at, for example https://prikboard.example.com'
  }
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && local)) return 'Use https (http only works for localhost)'
  if (url.search || url.hash || url.username) return 'Leave out the query, fragment and user name'
  return undefined
}

/** Whether a PrikBoard API answers at the address: its anonymous sign-in provider list. Returns a problem, or undefined. */
async function probe(address: string) {
  let response: Response
  try {
    response = await fetch(`${address}/api/auth/providers`, { signal: AbortSignal.timeout(10_000), headers: { Accept: 'application/json' } })
  } catch (error) {
    return `Could not reach ${address} (${errorMessage(error)})`
  }
  const body: unknown = response.ok ? await response.json().catch(() => null) : null
  return Array.isArray(body) ? undefined : `${address} does not look like PrikBoard (no sign-in at /api/auth/providers)`
}

/** Asks for the PrikBoard address until it is valid and PrikBoard answers there; undefined when canceled. */
function askForServerAddress() {
  return new Promise<string | undefined>((resolve) => {
    const box = vscode.window.createInputBox()
    box.title = 'PrikBoard address'
    box.prompt = 'The address you open PrikBoard at in your browser'
    box.placeholder = 'https://prikboard.example.com'
    box.value = serverChosen() ? serverUrl() : ''
    box.ignoreFocusOut = true
    let settled = false
    box.onDidChangeValue((value) => (box.validationMessage = value.trim() ? checkAddress(value) : undefined))
    box.onDidAccept(async () => {
      const candidate = box.value.trim().replace(/\/+$/, '')
      const invalid = checkAddress(candidate)
      if (invalid || box.busy) {
        box.validationMessage = invalid
        return
      }
      box.busy = true
      box.enabled = false
      const problem = await probe(candidate)
      box.busy = false
      box.enabled = true
      if (problem) {
        box.validationMessage = problem
        return
      }
      settled = true
      resolve(candidate)
      box.hide()
    })
    box.onDidHide(() => {
      if (!settled) resolve(undefined)
      box.dispose()
    })
    box.show()
  })
}

/** The sign-in through the issuer, from the settings; null while the authority or the client id is missing. */
function oidcConfig(): OidcConfig | null {
  const authority = setting('oidc.authority', '').trim()
  const clientId = setting('oidc.clientId', '').trim()
  if (!authority || !clientId) return null
  return {
    authority,
    clientId,
    scopes: setting('oidc.scopes', 'openid offline_access boards:read boards:write').trim(),
    audience: setting('oidc.audience', 'api://prikboard').trim(),
    redirectPort: setting('oidc.redirectPort', 0),
  }
}

/**
 * What requests are sent with: a personal access token (from PrikBoard's own sign-in, or pasted), or tokens from the
 * issuer's sign-in. `server` is the address it was made for, and an issuer's credential keeps the `oidc` sign-in it came
 * from, so changed settings never send it, or its refresh token, anywhere else. Credentials stored before these existed
 * lack them until `load` fills them in.
 */
type Credential =
  | { type: 'token'; token: string; server: string }
  | { type: 'oidc'; tokens: TokenSet; server: string; oidc: OidcConfig }

/**
 * Who is signed in and how requests prove it. The shared API client asks for the Authorization header on every request,
 * so a token from the issuer can be refreshed under it. Signing in through PrikBoard or the issuer acts as the person,
 * which "my work" needs; a pasted personal access token is usually an agent of its own.
 */
export class Auth implements vscode.Disposable {
  private credential: Credential | undefined
  private refreshing: Promise<void> | undefined
  private validating = false
  private whoAmI: Caller | undefined
  private readonly secrets: vscode.SecretStorage
  private readonly changed = new vscode.EventEmitter<boolean>()
  private readonly meChanged = new vscode.EventEmitter<void>()
  /** Fires with the new signed-in state. */
  readonly onDidChange = this.changed.event
  /** Fires when `me` has loaded or was cleared. */
  readonly onDidChangeMe = this.meChanged.event

  constructor(secrets: vscode.SecretStorage) {
    this.secrets = secrets
  }

  get signedIn() {
    return this.credential !== undefined
  }

  /** The signed-in caller from `whoAmI`; undefined until it has loaded. */
  get me() {
    return this.whoAmI
  }

  async init() {
    setUnauthorizedHandler(() => void this.rejected())
    this.credential = await this.load()
    this.reconfigure()
    await this.publish()
  }

  /**
   * Points the shared client at the configured server. The credential is read per request, so it can change under it.
   * A credential made for another server is dropped rather than sent there.
   */
  reconfigure() {
    if (this.credential && this.credential.server !== serverUrl()) {
      void this.signOut()
      void vscode.window.showWarningMessage('The PrikBoard address changed, so you were signed out of the old server. Sign in again.')
    }
    configureClient({
      baseUrl: serverUrl(),
      headers: async (): Promise<Record<string, string>> => {
        const authorization = await this.authorization()
        return authorization ? { Authorization: authorization } : {}
      },
    })
  }

  /**
   * Asks for the PrikBoard address, checks that PrikBoard answers there, and stores it in the user settings. The web
   * app and the API share it (nginx in a deployment, Vite's proxy in development), so a separate web address is
   * cleared. Signs out when the server changes, since the credential belongs to the old one. Returns false when canceled.
   */
  async chooseServer() {
    const address = await askForServerAddress()
    if (address === undefined) return false

    if (this.signedIn && address !== serverUrl()) await this.signOut()
    const configuration = vscode.workspace.getConfiguration('prikboard')
    await configuration.update('serverUrl', address, vscode.ConfigurationTarget.Global)
    await configuration.update('webUrl', undefined, vscode.ConfigurationTarget.Global)
    // Before a sign-in that follows straight away, not only when the configuration event arrives.
    this.reconfigure()
    return true
  }

  /** The server to sign in to: asks for one until PrikBoard answers there, so the browser never opens on a wrong address. */
  private async ensureServer() {
    if (!serverChosen()) return this.chooseServer()
    const problem = await probe(serverUrl())
    if (!problem) return true
    const choice = await vscode.window.showErrorMessage(problem, 'Change Address')
    return choice === 'Change Address' && this.chooseServer()
  }

  /** Signs in in the browser: through the identity provider when one is set up, otherwise through PrikBoard itself. */
  async signInWithBrowser() {
    if (!(await this.ensureServer())) return
    const config = oidcConfig()
    const server = serverUrl()
    let credential: Credential
    try {
      credential = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Waiting for the sign-in in your browser…', cancellable: true },
        async (_progress, cancelled): Promise<Credential> =>
          config
            ? { type: 'oidc', tokens: await signInWithBrowser(config, cancelled), server, oidc: config }
            : { type: 'token', token: await signInWithPrikBoard(webUrl(), cancelled), server },
      )
    } catch (error) {
      void vscode.window.showErrorMessage(`PrikBoard sign-in failed: ${errorMessage(error)}`)
      return
    }
    await this.adopt(credential)
  }

  /** Signs in with a personal access token the person created in PrikBoard. */
  async signInWithToken() {
    if (!(await this.ensureServer())) return
    const entered = await vscode.window.showInputBox({
      title: 'Sign in to PrikBoard',
      prompt: 'Paste a personal access token (PrikBoard: Account, Agents and access tokens)',
      password: true,
      ignoreFocusOut: true,
      validateInput: (value) => (value.trim() ? undefined : 'Paste the token'),
    })
    if (entered) await this.adopt({ type: 'token', token: entered.trim(), server: serverUrl() })
  }

  async signOut() {
    this.credential = undefined
    this.whoAmI = undefined
    this.meChanged.fire()
    await this.secrets.delete(CREDENTIAL_KEY)
    await this.secrets.delete(LEGACY_TOKEN_KEY)
    await this.publish()
  }

  /** Makes a new credential current once the server has accepted it. */
  private async adopt(credential: Credential) {
    const previous = this.credential
    this.credential = credential
    this.validating = true
    let caller: Caller
    try {
      caller = await api.whoAmI()
    } catch (error) {
      this.credential = previous
      void vscode.window.showErrorMessage(`PrikBoard did not accept the sign-in: ${errorMessage(error)}`)
      return
    } finally {
      this.validating = false
    }
    await this.secrets.store(CREDENTIAL_KEY, JSON.stringify(credential))
    await this.secrets.delete(LEGACY_TOKEN_KEY)
    this.whoAmI = caller
    this.meChanged.fire()
    await this.publish()
    void vscode.window.showInformationMessage(`Signed in to PrikBoard as ${caller.displayName}.`)
  }

  /**
   * The stored credential. One stored before credentials named their server belongs to the address in the user settings
   * (workspace settings can't set it), and an issuer's one to the sign-in configured now; it is stored again with them.
   */
  private async load(): Promise<Credential | undefined> {
    const stored = await this.secrets.get(CREDENTIAL_KEY)
    if (stored) {
      let credential: { type?: string; token?: string; tokens?: TokenSet; server?: string; oidc?: OidcConfig }
      try {
        credential = JSON.parse(stored) as typeof credential
      } catch {
        await this.secrets.delete(CREDENTIAL_KEY)
        return undefined
      }
      if (credential.server && (credential.type !== 'oidc' || credential.oidc)) return credential as Credential
      const server = credential.server ?? serverUrl()
      let bound: Credential | undefined
      if (credential.type === 'token' && credential.token) bound = { type: 'token', token: credential.token, server }
      const config = credential.type === 'oidc' ? oidcConfig() : null
      if (credential.type === 'oidc' && credential.tokens && config) bound = { type: 'oidc', tokens: credential.tokens, server, oidc: config }
      if (!bound) {
        await this.secrets.delete(CREDENTIAL_KEY)
        return undefined
      }
      await this.secrets.store(CREDENTIAL_KEY, JSON.stringify(bound))
      return bound
    }
    const legacy = await this.secrets.get(LEGACY_TOKEN_KEY)
    return legacy ? { type: 'token', token: legacy, server: serverUrl() } : undefined
  }

  /** The Authorization header value, refreshing the issuer's access token first when it is about to run out. */
  private async authorization() {
    const credential = this.credential
    // Never to another server than the one it was made for, even before reconfigure has signed out.
    if (!credential || credential.server !== serverUrl()) return undefined
    if (credential.type === 'token') return `Bearer ${credential.token}`

    if (credential.tokens.expiresAt - REFRESH_MARGIN_MS < Date.now()) {
      this.refreshing ??= this.refresh(credential).finally(() => (this.refreshing = undefined))
      await this.refreshing
    }
    return this.credential?.type === 'oidc' ? `Bearer ${this.credential.tokens.accessToken}` : undefined
  }

  /** Renews the issuer's tokens at the issuer they came from, not whatever the settings say now. */
  private async refresh(credential: Extract<Credential, { type: 'oidc' }>) {
    const { tokens, oidc, server } = credential
    try {
      if (!tokens.refreshToken) throw new Error('There is no refresh token.')
      const renewed = await refreshTokens(oidc, tokens.refreshToken)
      this.credential = { type: 'oidc', tokens: renewed, server, oidc }
      await this.secrets.store(CREDENTIAL_KEY, JSON.stringify(this.credential))
    } catch (error) {
      await this.signOut()
      void vscode.window.showWarningMessage(`Your PrikBoard sign-in ended (${errorMessage(error)}). Sign in again.`)
    }
  }

  private async rejected() {
    // A wrong credential is reported where it was entered.
    if (this.validating || !this.credential) return
    await this.signOut()
    void vscode.window.showWarningMessage('PrikBoard rejected the sign-in. Sign in again.')
  }

  private async publish() {
    await vscode.commands.executeCommand('setContext', 'prikboard.signedIn', this.signedIn)
    this.changed.fire(this.signedIn)
    if (this.signedIn && !this.whoAmI) void this.loadMe()
  }

  /** Looks up who is signed in; "my work" needs it, so a failure only leaves it unknown. */
  private async loadMe() {
    try {
      this.whoAmI = await api.whoAmI()
      this.meChanged.fire()
    } catch {
      // The tree reports connection problems; an unknown caller only hides what depends on it.
    }
  }

  dispose() {
    this.changed.dispose()
    this.meChanged.dispose()
  }
}
