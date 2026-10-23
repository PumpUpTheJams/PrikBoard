import { useEffect, useState } from 'react'
import { api, errorMessage, loginUrl } from '../api/client'
import type { AuthProvider } from '../api/types'
import { CardIcon } from '../board/icons'

/** authError: why the last sign-in failed, from takeAuthError. */
export function SignIn({ authError }: { authError: string | null }) {
  const [providers, setProviders] = useState<AuthProvider[] | null>(null)
  const [error, setError] = useState(authError)

  useEffect(() => {
    api.listAuthProviders().then(setProviders, (e) => setError(errorMessage(e)))
  }, [])

  // Come back to the board that was open, which lives in the hash.
  const returnUrl = `/${window.location.hash}`

  return (
    <div className="sign-in">
      <div className="sign-in-card">
        <span className="board-tile large" aria-hidden="true">
          <CardIcon size={28} />
        </span>
        <h1>PrikBoard</h1>
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
            No sign-in providers are set up, so nobody can sign in. An admin can add one by restarting PrikBoard with{' '}
            <code>Setup__Recovery=true</code> and opening <a href="#/setup">the setup</a>.
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
