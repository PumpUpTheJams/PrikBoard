import * as vscode from 'vscode'
import { api, configureClient, errorMessage, setUnauthorizedHandler } from '../../web/src/api/client'

const SECRET_KEY = 'prikboard.accessToken'

export function setting<T>(name: string, fallback: T): T {
  return vscode.workspace.getConfiguration('prikboard').get<T>(name, fallback)
}

/** The API origin without a trailing slash. */
export const serverUrl = () => setting('serverUrl', 'http://localhost:5080').replace(/\/+$/, '')

/** Where the web app runs; defaults to the API origin, which serves it in a deployment. */
export const webUrl = () => (setting('webUrl', '') || serverUrl()).replace(/\/+$/, '')

/**
 * The signed-in state and the credential the shared API client sends. Today the credential is a personal access token
 * pasted in by the person; everything else only uses `signedIn`, so a sign-in through the identity provider can replace it.
 */
export class Auth implements vscode.Disposable {
  private token: string | undefined
  private validating = false
  private readonly changed = new vscode.EventEmitter<boolean>()
  /** Fires with the new signed-in state. */
  readonly onDidChange = this.changed.event

  private readonly secrets: vscode.SecretStorage

  constructor(secrets: vscode.SecretStorage) {
    this.secrets = secrets
  }

  get signedIn() {
    return this.token !== undefined
  }

  async init() {
    this.reconfigure()
    setUnauthorizedHandler(() => void this.rejected())
    this.token = await this.secrets.get(SECRET_KEY)
    await this.publish()
  }

  /** Points the shared client at the configured server. The token is read per request, so it can change under it. */
  reconfigure() {
    configureClient({
      baseUrl: serverUrl(),
      headers: (): Record<string, string> => (this.token ? { Authorization: `Bearer ${this.token}` } : {}),
    })
  }

  async signIn() {
    const entered = await vscode.window.showInputBox({
      title: 'Sign in to PrikBoard',
      prompt: 'Paste a personal access token (PrikBoard: Account, Agents and access tokens)',
      password: true,
      ignoreFocusOut: true,
      validateInput: (value) => (value.trim() ? undefined : 'Paste the token'),
    })
    if (!entered) return

    const previous = this.token
    this.token = entered.trim()
    this.validating = true
    try {
      await api.whoAmI()
    } catch (error) {
      this.token = previous
      void vscode.window.showErrorMessage(`PrikBoard did not accept the token: ${errorMessage(error)}`)
      return
    } finally {
      this.validating = false
    }
    await this.secrets.store(SECRET_KEY, this.token)
    await this.publish()
  }

  async signOut() {
    this.token = undefined
    await this.secrets.delete(SECRET_KEY)
    await this.publish()
  }

  private async rejected() {
    // A wrong token pasted during sign-in is reported there.
    if (this.validating || !this.token) return
    await this.signOut()
    void vscode.window.showWarningMessage('PrikBoard rejected the access token. Sign in again.')
  }

  private async publish() {
    await vscode.commands.executeCommand('setContext', 'prikboard.signedIn', this.signedIn)
    this.changed.fire(this.signedIn)
  }

  dispose() {
    this.changed.dispose()
  }
}
