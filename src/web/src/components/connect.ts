/** What an app (the VS Code extension) put in the link it opened: #/connect?state=…&code_challenge=…&name=…[&redirect_uri=…]. */
export interface ConnectRequest {
  state: string
  codeChallenge: string
  name: string
  /** A loopback address the app listens on; absent when the code is to be pasted into the app. */
  redirectUri: string | null
}

/** The app's sign-in request from the hash, or null when this is not one. */
export function connectFromHash(): ConnectRequest | null {
  const match = window.location.hash.match(/^#\/connect\?(.*)$/)
  if (!match) return null
  const params = new URLSearchParams(match[1])
  return {
    state: params.get('state') ?? '',
    codeChallenge: params.get('code_challenge') ?? '',
    name: (params.get('name') ?? '').slice(0, 100).trim() || 'An app',
    redirectUri: params.get('redirect_uri'),
  }
}
