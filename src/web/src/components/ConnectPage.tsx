import { useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { Caller } from '../api/types'
import { CardIcon } from '../board/icons'
import type { ConnectRequest } from './connect'

/** Same rule as the server (AppSignInEndpoints.IsLoopback): the app on this machine, never another site. */
function isLoopback(address: string) {
  try {
    const url = new URL(address)
    return url.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) && url.port !== '' && !url.username
  } catch {
    return false
  }
}

/** The app's address with the outcome added, as an OAuth redirect would carry it. */
function redirectTo(redirectUri: string, params: Record<string, string>) {
  const url = new URL(redirectUri)
  for (const [key, value] of Object.entries(params)) url.searchParams.set(key, value)
  window.location.replace(url.toString())
}

interface ConnectPageProps {
  request: ConnectRequest
  user: Caller
  onDone: () => void
}

/**
 * Opened by an app (the VS Code extension) once signed in: asks whether it may use PrikBoard as you. Allowing gets a
 * one-time code, which goes back to the app through its loopback address or is shown to paste. The code only works
 * together with a secret the app kept, so it is useless to anyone else.
 */
export function ConnectPage({ request, user, onDone }: ConnectPageProps) {
  const [code, setCode] = useState<string | null>(null)
  const [returning, setReturning] = useState(false)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const paste = request.redirectUri === null
  const complete = request.state !== '' && request.codeChallenge !== ''

  async function allow() {
    setBusy(true)
    setError(null)
    try {
      const approved = await api.approveAppSignIn({
        name: request.name,
        codeChallenge: request.codeChallenge,
        redirectUri: request.redirectUri ?? undefined,
      })
      if (request.redirectUri) {
        setReturning(true)
        redirectTo(request.redirectUri, { code: approved.code, state: request.state })
      } else {
        setCode(approved.code)
      }
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  function cancel() {
    // Tell a waiting app, so it stops waiting. The address is unchecked here, so only ever a loopback one.
    if (request.redirectUri && complete && isLoopback(request.redirectUri)) {
      redirectTo(request.redirectUri, { error: 'access_denied', state: request.state })
      return
    }
    onDone()
  }

  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <div className="sign-in">
      <div className="sign-in-card">
        <span className="board-tile large" aria-hidden="true">
          <CardIcon size={28} />
        </span>
        {!complete ? (
          <>
            <h1>Sign-in link incomplete</h1>
            <p>This link is missing parts. Start signing in again from VS Code.</p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Go to your boards
              </button>
            </div>
          </>
        ) : returning ? (
          <>
            <h1>Signed in</h1>
            <p>Returning to VS Code. You can close this tab.</p>
          </>
        ) : code ? (
          <>
            <h1>Paste this code into VS Code</h1>
            <div className="copy-field">
              <input readOnly aria-label="Sign-in code" value={code} onFocus={(e) => e.target.select()} />
              <button type="button" className="ghost small" onClick={() => void copy(code)}>
                {copied ? 'Copied' : 'Copy'}
              </button>
            </div>
            <p className="sign-in-error" role="alert">
              Only paste it into VS Code that you started yourself. PrikBoard never asks you to send it to anyone. It works once, for
              5 minutes.
            </p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Done
              </button>
            </div>
          </>
        ) : (
          <>
            <h1>Allow VS Code to use PrikBoard as you?</h1>
            <p>
              An app calling itself <strong>“{request.name}”</strong> asks to sign in as <strong>{user.displayName}</strong>. It can
              see and change everything you can, on all your boards, for 90 days. You can revoke it any time under Account, Agents
              and access tokens.
            </p>
            {paste && (
              <p className="sign-in-error">
                Only continue if you just started signing in from VS Code yourself. If someone sent you this link, cancel.
              </p>
            )}
            {error && (
              <p className="sign-in-error" role="alert">
                {error}
              </p>
            )}
            <div className="sign-in-providers">
              <button type="button" className="primary" disabled={busy} onClick={() => void allow()}>
                Allow
              </button>
              <button type="button" className="ghost" disabled={busy} onClick={cancel}>
                Cancel
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
