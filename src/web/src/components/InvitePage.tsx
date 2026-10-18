import { useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { InvitePreview } from '../api/types'
import { PostItIcon } from '../board/icons'

interface InvitePageProps {
  token: string
  /** Accepted (or already a member): go to the board. */
  onJoined: (boardId: string) => void
  onDismiss: () => void
}

/** Opened from an invite link (#/invite/{token}) once signed in: shows the board and role, and accepts on request. */
export function InvitePage({ token, onJoined, onDismiss }: InvitePageProps) {
  const [preview, setPreview] = useState<InvitePreview | null | undefined>(undefined)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.previewInvite(token).then(setPreview, () => setPreview(null))
  }, [token])

  async function accept() {
    setBusy(true)
    try {
      onJoined((await api.acceptInvite(token)).boardId)
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  return (
    <div className="sign-in">
      <div className="sign-in-card">
        <span className="board-tile large" aria-hidden="true">
          <PostItIcon size={28} />
        </span>
        {preview === undefined ? (
          <p className="status-inline">Checking the invite…</p>
        ) : preview === null ? (
          <>
            <h1>Invite not found</h1>
            <p>This invite link is invalid, expired or already used. Ask the board's owner for a new one.</p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDismiss}>
                Go to your boards
              </button>
            </div>
          </>
        ) : (
          <>
            <h1>{preview.boardName}</h1>
            <p>
              {preview.invitedBy ? `${preview.invitedBy} invited you` : 'You are invited'} to join as{' '}
              <strong>{preview.role.toLowerCase()}</strong>.
              {preview.yourRole && ` You are already a ${preview.yourRole.toLowerCase()} here.`}
            </p>
            {error && (
              <p className="sign-in-error" role="alert">
                {error}
              </p>
            )}
            <div className="sign-in-providers">
              <button type="button" className="primary" disabled={busy} onClick={() => void accept()}>
                Join board
              </button>
              <button type="button" className="ghost" onClick={onDismiss}>
                Not now
              </button>
            </div>
            <p className="dialog-hint">The link works once and expires {new Date(preview.expiresAt).toLocaleDateString()}.</p>
          </>
        )}
      </div>
    </div>
  )
}
