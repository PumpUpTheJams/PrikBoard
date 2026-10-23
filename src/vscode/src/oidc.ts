import { createHash, randomBytes } from 'node:crypto'
import * as http from 'node:http'
import type { AddressInfo } from 'node:net'
import * as vscode from 'vscode'

/** An OpenID Connect sign-in with authorization code and PKCE, for a public client (no secret) with a loopback redirect. */
export interface OidcConfig {
  authority: string
  clientId: string
  /** Space separated. Must include what the API needs (boards:read, boards:write), as the issuer names it. */
  scopes: string
  /** Sent as `audience` (Auth0 style). Leave empty for issuers that take the audience from the scopes. */
  audience: string
  /** Fixed loopback port when the issuer needs an exact redirect URI; 0 picks a free one. */
  redirectPort: number
}

export interface TokenSet {
  accessToken: string
  refreshToken?: string
  /** Milliseconds since the epoch. */
  expiresAt: number
}

interface Discovery {
  authorization_endpoint: string
  token_endpoint: string
}

const SIGN_IN_TIMEOUT_MS = 5 * 60 * 1000

export const base64url = (bytes: Buffer) => bytes.toString('base64url')

/** Endpoints carry the person's credentials, so they must be encrypted unless they are on this machine. */
function assertSecure(url: string) {
  const { protocol, hostname } = new URL(url)
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(hostname)
  if (protocol !== 'https:' && !(protocol === 'http:' && local)) {
    throw new Error(`${url} must use https.`)
  }
}

async function discover(authority: string): Promise<Discovery> {
  const url = `${authority.replace(/\/+$/, '')}/.well-known/openid-configuration`
  assertSecure(url)
  const response = await fetch(url)
  if (!response.ok) throw new Error(`Could not read ${url} (${response.status}).`)

  const document = (await response.json()) as Partial<Discovery>
  if (!document.authorization_endpoint || !document.token_endpoint) {
    throw new Error(`${url} does not name an authorization and a token endpoint.`)
  }
  assertSecure(document.authorization_endpoint)
  assertSecure(document.token_endpoint)
  return { authorization_endpoint: document.authorization_endpoint, token_endpoint: document.token_endpoint }
}

async function requestTokens(endpoint: string, form: Record<string, string>, previousRefreshToken?: string): Promise<TokenSet> {
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    body: new URLSearchParams(form),
  })
  const body = (await response.json().catch(() => null)) as {
    access_token?: string
    refresh_token?: string
    expires_in?: number
    error?: string
    error_description?: string
  } | null

  if (!response.ok || !body?.access_token) {
    throw new Error(body?.error_description ?? body?.error ?? `The token endpoint answered ${response.status}.`)
  }
  return {
    accessToken: body.access_token,
    // Issuers that rotate refresh tokens send a new one; the others expect the old one to be used again.
    refreshToken: body.refresh_token ?? previousRefreshToken,
    expiresAt: Date.now() + (body.expires_in ?? 3600) * 1000,
  }
}

export const page = (message: string) =>
  `<!doctype html><meta charset="utf-8"><title>PrikBoard</title>` +
  `<body style="font:16px system-ui;margin:3rem auto;max-width:28rem"><h1>PrikBoard</h1><p>${message}</p></body>`

/** Listens on the loopback interface for the redirect and resolves with the authorization code. Also used by `connect.ts`. */
export async function listenForCode(port: number, state: string, signal: AbortSignal) {
  const server = http.createServer()
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject)
    server.listen(port, '127.0.0.1', resolve)
  })
  const redirectUri = `http://127.0.0.1:${(server.address() as AddressInfo).port}/callback`

  const code = new Promise<string>((resolve, reject) => {
    signal.addEventListener('abort', () => reject(new Error('Sign-in was cancelled or timed out.')), { once: true })
    server.on('request', (request, response) => {
      const url = new URL(request.url ?? '/', 'http://127.0.0.1')
      // Anything that is not the redirect of this sign-in is ignored, so a stray request cannot end it.
      if (url.pathname !== '/callback' || url.searchParams.get('state') !== state) {
        response.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' }).end(page('This is not a response to the sign-in.'))
        return
      }

      const error = url.searchParams.get('error')
      const authorizationCode = url.searchParams.get('code')
      if (error || !authorizationCode) {
        response.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' }).end(page('Sign-in failed. Go back to VS Code for details.'))
        reject(new Error(url.searchParams.get('error_description') ?? error ?? 'The issuer sent no authorization code.'))
        return
      }
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }).end(page('You are signed in. You can close this tab and go back to VS Code.'))
      resolve(authorizationCode)
    })
  })

  // Canceling can happen before the caller starts waiting; the caller still gets the rejection when it does.
  code.catch(() => undefined)

  const close = () => {
    server.closeAllConnections()
    server.close()
  }
  return { redirectUri, code, close }
}

/** Opens the browser for the person to sign in, and exchanges the code that comes back for tokens. */
export async function signInWithBrowser(config: OidcConfig, cancelled: vscode.CancellationToken): Promise<TokenSet> {
  const endpoints = await discover(config.authority)

  const verifier = base64url(randomBytes(32))
  const state = base64url(randomBytes(16))
  const abort = new AbortController()
  const timeout = setTimeout(() => abort.abort(), SIGN_IN_TIMEOUT_MS)
  const cancel = cancelled.onCancellationRequested(() => abort.abort())
  const listener = await listenForCode(config.redirectPort, state, abort.signal)

  try {
    const authorize = new URL(endpoints.authorization_endpoint)
    authorize.search = new URLSearchParams({
      response_type: 'code',
      client_id: config.clientId,
      redirect_uri: listener.redirectUri,
      scope: config.scopes,
      state,
      code_challenge: base64url(createHash('sha256').update(verifier).digest()),
      code_challenge_method: 'S256',
      ...(config.audience ? { audience: config.audience } : {}),
    }).toString()
    await vscode.env.openExternal(vscode.Uri.parse(authorize.toString(), true))

    const code = await listener.code
    return await requestTokens(endpoints.token_endpoint, {
      grant_type: 'authorization_code',
      code,
      redirect_uri: listener.redirectUri,
      client_id: config.clientId,
      code_verifier: verifier,
    })
  } finally {
    clearTimeout(timeout)
    cancel.dispose()
    listener.close()
  }
}

/** Trades the refresh token for new tokens. */
export async function refreshTokens(config: OidcConfig, refreshToken: string): Promise<TokenSet> {
  const endpoints = await discover(config.authority)
  return requestTokens(
    endpoints.token_endpoint,
    { grant_type: 'refresh_token', refresh_token: refreshToken, client_id: config.clientId },
    refreshToken,
  )
}
