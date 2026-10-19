import * as vscode from 'vscode'
import { api, configureClient, errorMessage, setUnauthorizedHandler } from '../../web/src/api/client'
import type { Caller } from '../../web/src/api/types'
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

/** What requests are sent with: a pasted personal access token, or tokens from the issuer's sign-in. */
type Credential = { type: 'token'; token: string } | { type: 'oidc'; tokens: TokenSet }

/**
 * Who is signed in and how requests prove it. The shared API client asks for the Authorization header on every request,
 * so a token from the issuer can be refreshed under it. Only a sign-in through the issuer says who the person is
 * (a personal access token is an agent of its own), which "my work" needs.
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
    this.reconfigure()
    setUnauthorizedHandler(() => void this.rejected())
    this.credential = await this.load()
    await this.publish()
  }

  /** Points the shared client at the configured server. The credential is read per request, so it can change under it. */
  reconfigure() {
    configureClient({
      baseUrl: serverUrl(),
      headers: async (): Promise<Record<string, string>> => {
        const authorization = await this.authorization()
        return authorization ? { Authorization: authorization } : {}
      },
    })
  }

  /** Signs in through the issuer in the browser. */
  async signInWithBrowser() {
    const config = oidcConfig()
    if (!config) {
      const choice = await vscode.window.showInformationMessage(
        'Signing in through your identity provider needs prikboard.oidc.authority and prikboard.oidc.clientId.',
        'Open Settings',
        'Use an Access Token',
      )
      if (choice === 'Open Settings') await vscode.commands.executeCommand('workbench.action.openSettings', 'prikboard.oidc')
      if (choice === 'Use an Access Token') await this.signInWithToken()
      return
    }

    let tokens: TokenSet
    try {
      tokens = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Waiting for the sign-in in your browser…', cancellable: true },
        (_progress, cancelled) => signInWithBrowser(config, cancelled),
      )
    } catch (error) {
      void vscode.window.showErrorMessage(`PrikBoard sign-in failed: ${errorMessage(error)}`)
      return
    }
    await this.adopt({ type: 'oidc', tokens })
  }

  /** Signs in with a personal access token the person created in PrikBoard. */
  async signInWithToken() {
    const entered = await vscode.window.showInputBox({
      title: 'Sign in to PrikBoard',
      prompt: 'Paste a personal access token (PrikBoard: Account, Agents and access tokens)',
      password: true,
      ignoreFocusOut: true,
      validateInput: (value) => (value.trim() ? undefined : 'Paste the token'),
    })
    if (entered) await this.adopt({ type: 'token', token: entered.trim() })
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

  private async load(): Promise<Credential | undefined> {
    const stored = await this.secrets.get(CREDENTIAL_KEY)
    if (stored) {
      try {
        return JSON.parse(stored) as Credential
      } catch {
        await this.secrets.delete(CREDENTIAL_KEY)
      }
    }
    const legacy = await this.secrets.get(LEGACY_TOKEN_KEY)
    return legacy ? { type: 'token', token: legacy } : undefined
  }

  /** The Authorization header value, refreshing the issuer's access token first when it is about to run out. */
  private async authorization() {
    const credential = this.credential
    if (!credential) return undefined
    if (credential.type === 'token') return `Bearer ${credential.token}`

    if (credential.tokens.expiresAt - REFRESH_MARGIN_MS < Date.now()) {
      this.refreshing ??= this.refresh(credential.tokens).finally(() => (this.refreshing = undefined))
      await this.refreshing
    }
    return this.credential?.type === 'oidc' ? `Bearer ${this.credential.tokens.accessToken}` : undefined
  }

  private async refresh(tokens: TokenSet) {
    const config = oidcConfig()
    try {
      if (!config || !tokens.refreshToken) throw new Error('There is no refresh token.')
      const renewed = await refreshTokens(config, tokens.refreshToken)
      this.credential = { type: 'oidc', tokens: renewed }
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
