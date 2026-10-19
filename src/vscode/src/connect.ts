import { createHash, randomBytes } from 'node:crypto'
import { hostname } from 'node:os'
import * as vscode from 'vscode'
import { api } from '../../web/src/api/client'
import { base64url, listenForCode } from './oidc'

const SIGN_IN_TIMEOUT_MS = 5 * 60 * 1000

/** What the token is called in PrikBoard, for example "Visual Studio Code on laptop". */
const appName = () => `${vscode.env.appName} on ${hostname()}`.slice(0, 100)

/** The address the browser uses for our listener, when it is still this machine (Remote SSH forwards it); not in Codespaces. */
async function browserAddress(redirectUri: string) {
  const external = await vscode.env.asExternalUri(vscode.Uri.parse(redirectUri))
  const loopback = external.scheme === 'http' && ['127.0.0.1', 'localhost', '[::1]'].includes(external.authority.replace(/:\d+$/, ''))
  return loopback ? external.toString(true) : undefined
}

const connectUrl = (webUrl: string, params: Record<string, string>) => `${webUrl}/#/connect?${new URLSearchParams(params).toString()}`

/**
 * Signs in through PrikBoard itself, like Claude Code: the browser opens PrikBoard, the person allows the sign-in, and
 * PrikBoard hands back a one-time code, through a loopback redirect or by pasting. The code is traded with the PKCE
 * verifier, which never leaves this process, for a personal access token that acts as the person.
 */
export async function signInWithPrikBoard(webUrl: string, cancelled: vscode.CancellationToken): Promise<string> {
  const verifier = base64url(randomBytes(32))
  const state = base64url(randomBytes(16))
  const params = { state, code_challenge: base64url(createHash('sha256').update(verifier).digest()), name: appName() }

  const abort = new AbortController()
  const timeout = setTimeout(() => abort.abort(), SIGN_IN_TIMEOUT_MS)
  const cancel = cancelled.onCancellationRequested(() => abort.abort())
  const listener = await listenForCode(0, state, abort.signal)
  let pasteBox: vscode.InputBox | undefined

  /** Shows the code in the browser instead, and asks for it here. */
  const pasted = async () => {
    await vscode.env.openExternal(vscode.Uri.parse(connectUrl(webUrl, params), true))
    return new Promise<string>((resolve, reject) => {
      const box = (pasteBox = vscode.window.createInputBox())
      box.title = 'Sign in to PrikBoard'
      box.prompt = 'Allow the sign-in in your browser, then paste the code it shows'
      box.password = true
      box.ignoreFocusOut = true
      box.onDidAccept(() => {
        if (box.value.trim()) resolve(box.value.trim())
      })
      box.onDidHide(() => reject(new Error('Sign-in was cancelled.')))
      abort.signal.addEventListener('abort', () => reject(new Error('Sign-in was cancelled or timed out.')), { once: true })
      box.show()
    })
  }

  try {
    const redirectUri = await browserAddress(listener.redirectUri)
    let code: Promise<string>
    if (redirectUri) {
      await vscode.env.openExternal(vscode.Uri.parse(connectUrl(webUrl, { ...params, redirect_uri: redirectUri }), true))
      // A browser that cannot reach VS Code (another machine, a strict firewall) can show the code instead.
      const fallback = vscode.window
        .showInformationMessage('Allow the sign-in in your browser. Does it not come back to VS Code?', 'Paste a Code')
        .then((choice) => (choice === 'Paste a Code' && !abort.signal.aborted ? pasted() : new Promise<string>(() => {})))
      code = Promise.race([listener.code, fallback])
    } else {
      code = pasted()
    }

    let received: string
    try {
      received = await code
    } catch (error) {
      if (error instanceof Error && error.message === 'access_denied') throw new Error('The sign-in was cancelled in the browser.')
      throw error
    }
    return (await api.exchangeAppSignIn(received, verifier)).token
  } finally {
    clearTimeout(timeout)
    cancel.dispose()
    listener.close()
    pasteBox?.dispose()
    abort.abort()
  }
}
