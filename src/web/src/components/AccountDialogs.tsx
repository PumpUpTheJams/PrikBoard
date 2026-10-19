import { useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage, linkUrl } from '../api/client'
import type { AuthProvider, BoardSummary, Caller } from '../api/types'
import { AccessTokens } from './AccessTokens'
import { Face } from './Avatar'
import { ConfirmDialog } from './ConfirmDialog'
import { Dialog } from './Dialog'

/**
 * Asked once after signing in, while no name is chosen: the provider's name (and picture) are only suggestions and are
 * not stored unless the person accepts them (or types another name).
 */
export function DisplayNameDialog({ user, onSaved }: { user: Caller; onSaved: (user: Caller) => void }) {
  const [name, setName] = useState(user.suggestedDisplayName ?? '')
  const offerPicture = user.suggestedAvatarUrl !== null && user.avatarUrl === null
  const [usePicture, setUsePicture] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function save(displayName: string, withPicture = false) {
    try {
      const named = await api.updateMe({ displayName })
      onSaved(withPicture ? await api.useSignInAvatar() : named)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    void save(name.trim(), offerPicture && usePicture)
  }

  return (
    <Dialog title="What should others see?" onClose={() => void save('')}>
      <form className="dialog-body" onSubmit={submit}>
        <p className="dialog-text">
          PrikBoard doesn't store your name or email from {user.provider ?? 'your sign-in'}. Pick the name people see on
          shared boards; you can change it any time.
        </p>
        <input autoFocus aria-label="Display name" value={name} maxLength={100} placeholder="Your name" onChange={(e) => setName(e.target.value)} />
        {offerPicture && (
          <label className="picture-choice">
            <input type="checkbox" checked={usePicture} onChange={(e) => setUsePicture(e.target.checked)} />
            <span className="avatar avatar-lg" aria-hidden="true">
              <Face name={name || user.displayName} avatarUrl={user.suggestedAvatarUrl} />
            </span>
            Also show my {providerLabel(user)} picture
          </label>
        )}
        {error && <p className="widget-error">{error}</p>}
        <div className="dialog-actions">
          <button type="button" className="ghost" onClick={() => void save('')}>
            Not now
          </button>
          <button type="submit" className="primary" disabled={!name.trim()}>
            Use this name
          </button>
        </div>
      </form>
    </Dialog>
  )
}

/** The provider a session signed in with, for labels such as "your GitHub picture". */
function providerLabel(user: Caller, providers: AuthProvider[] = []) {
  const id = user.provider ?? ''
  return providers.find((p) => p.id === id)?.displayName ?? (id === 'github' ? 'GitHub' : 'sign-in')
}

interface AccountDialogProps {
  user: Caller
  /** Your boards, to limit access tokens to some of them. */
  boards: BoardSummary[]
  onChange: (user: Caller) => void
  onClose: () => void
}

/** Display name, linked sign-ins and access tokens. Connecting a sign-in is a full-page trip to the provider and back. */
export function AccountDialog({ user, boards, onChange, onClose }: AccountDialogProps) {
  const [name, setName] = useState(user.chosenDisplayName ?? '')
  const [providers, setProviders] = useState<AuthProvider[]>([])
  const [unlinking, setUnlinking] = useState<Caller['identities'][number] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const dirty = name.trim() !== (user.chosenDisplayName ?? '')

  useEffect(() => {
    api.listAuthProviders().then(setProviders, (e) => setError(errorMessage(e)))
  }, [])

  async function saveName(event: FormEvent) {
    event.preventDefault()
    try {
      onChange(await api.updateMe({ displayName: name.trim() }))
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  async function changePicture(take: boolean) {
    try {
      onChange(await (take ? api.useSignInAvatar() : api.removeAvatar()))
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  async function unlink(identityId: string) {
    setUnlinking(null)
    try {
      await api.unlinkIdentity(identityId)
      onChange(await api.whoAmI())
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <Dialog title="Account" subtitle={`Shown to others as ${user.displayName}`} onClose={onClose} wide>
      <div className="dialog-body">
        <form className="dialog-section" onSubmit={saveName}>
          <h3>Display name</h3>
          <div className="inline-form">
            <input aria-label="Display name" value={name} maxLength={100} placeholder={user.displayName} onChange={(e) => setName(e.target.value)} />
            <button type="submit" className="primary" disabled={!dirty}>
              Save
            </button>
          </div>
          <p className="dialog-hint">Leave it empty to appear as “User {user.id.replace(/-/g, '').slice(-4)}”.</p>
        </form>

        <section className="dialog-section">
          <h3>Picture</h3>
          <div className="inline-form picture-row">
            <span className="avatar avatar-lg" aria-hidden="true">
              <Face name={user.displayName} avatarUrl={user.avatarUrl} />
            </span>
            {user.suggestedAvatarUrl !== null && (
              <button type="button" className="ghost small" onClick={() => void changePicture(true)}>
                {user.avatarUrl ? `Update from ${providerLabel(user, providers)}` : `Use my ${providerLabel(user, providers)} picture`}
              </button>
            )}
            {user.avatarUrl !== null && (
              <button type="button" className="ghost small" onClick={() => void changePicture(false)}>
                Remove
              </button>
            )}
          </div>
          <p className="dialog-hint">
            {user.suggestedAvatarUrl === null && user.avatarUrl === null
              ? 'Sign in with GitHub to use your GitHub picture; until then others see your initials.'
              : 'PrikBoard keeps a copy of the picture only once you pick it, and you can remove it any time.'}
          </p>
        </section>

        <section className="dialog-section">
          <h3>Sign-ins</h3>
          <p className="dialog-hint">Any of these signs you in to this same account. PrikBoard keeps only a fingerprint of each, never your name or email.</p>
          <ul className="row-list">
            {user.identities.map((identity) => (
              <li key={identity.id}>
                <span className="row-main">{identity.providerName}</span>
                <span className="row-meta">since {new Date(identity.linkedAt).toLocaleDateString()}</span>
                <button
                  type="button"
                  className="ghost small"
                  disabled={user.identities.length === 1}
                  title={user.identities.length === 1 ? 'Your only sign-in can’t be removed' : undefined}
                  onClick={() => setUnlinking(identity)}
                >
                  Unlink
                </button>
              </li>
            ))}
          </ul>
          {providers.length > 0 && (
            <div className="connect-row">
              <span className="dialog-hint">Connect another:</span>
              {providers.map((p) => (
                // A real form post: the browser has to travel to the provider and back.
                <form key={p.id} method="post" action={linkUrl(p.id)}>
                  <input type="hidden" name="returnUrl" value={`/${window.location.hash}`} />
                  <button type="submit" className="ghost small">
                    {p.displayName}
                  </button>
                </form>
              ))}
            </div>
          )}
        </section>

        <AccessTokens boards={boards} />
        {error && <p className="widget-error">{error}</p>}
      </div>

      {unlinking && (
        <ConfirmDialog
          title={`Unlink ${unlinking.providerName}?`}
          message={`You won't be able to sign in to this account with ${unlinking.providerName} any more. You can connect it again later.`}
          confirmLabel="Unlink"
          onConfirm={() => void unlink(unlinking.id)}
          onCancel={() => setUnlinking(null)}
        />
      )}
    </Dialog>
  )
}
