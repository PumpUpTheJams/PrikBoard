import { useEffect, useState } from 'react'
import { api, errorMessage, loginUrl } from '../api/client'
import type { AuthProvider } from '../api/types'

const authErrors: Record<string, string> = {
  not_allowed: 'That account is not allowed to use this PrikBoard. Ask an administrator to add your email.',
  failed: 'Signing in did not work. Please try again.',
}

/** Reads and removes ?authError=… which the API adds when it sends a failed sign-in back here. */
function takeAuthError(): string | null {
  const url = new URL(window.location.href)
  const code = url.searchParams.get('authError')
  if (code === null) return null
  url.searchParams.delete('authError')
  window.history.replaceState(null, '', url)
  return authErrors[code] ?? authErrors.failed
}

export function SignIn() {
  const [providers, setProviders] = useState<AuthProvider[] | null>(null)
  const [error, setError] = useState(takeAuthError)

  useEffect(() => {
    api.listAuthProviders().then(setProviders, (e) => setError(errorMessage(e)))
  }, [])

  // Come back to the board that was open, which lives in the hash.
  const returnUrl = `/${window.location.hash}`

  return (
    <div className="sign-in">
      <div className="sign-in-card">
        <h1 className="brand">
          <span className="brand-mark" aria-hidden="true" />
          PrikBoard
        </h1>
        <p>Sign in to see your boards.</p>
        {error && (
          <p className="sign-in-error" role="alert">
            {error}
          </p>
        )}
        {providers === null ? (
          <p className="status-inline">Loading…</p>
        ) : providers.length === 0 ? (
          <p className="status-inline">
            No sign-in providers are configured. Set up GitHub sign-in as described in{' '}
            <code>docs/authentication.md</code>.
          </p>
        ) : (
          <div className="sign-in-providers">
            {providers.map((p, i) => (
              // The API lists the default provider first.
              <a key={p.id} className={i === 0 ? 'button primary' : 'button'} href={loginUrl(p.id, returnUrl)}>
                Continue with {p.displayName}
              </a>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
