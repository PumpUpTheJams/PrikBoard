import { useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { Caller, DiscordAccountLinkPreview } from '../api/types'
import { CardIcon } from '../board/icons'

interface DiscordAccountPageProps {
  code: string
  user: Caller
  onDone: () => void
}

/**
 * Opened from the Discord bot's "Connect your PrikBoard account" button: links that Discord account to you, so Save to
 * PrikBoard there works as you. The page names the Discord account the code is for, so nobody can link theirs to you by
 * sending you their link.
 */
export function DiscordAccountPage({ code, user, onDone }: DiscordAccountPageProps) {
  // A link without a code has nothing to look up: it shows as expired straight away.
  const [preview, setPreview] = useState<DiscordAccountLinkPreview | null | undefined>(code === '' ? null : undefined)
  const [linked, setLinked] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (code !== '') api.previewDiscordAccountLink(code).then(setPreview, () => setPreview(null))
  }, [code])

  async function link() {
    setBusy(true)
    setError(null)
    try {
      await api.linkDiscordAccount(code)
      setLinked(true)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="sign-in">
      <div className="sign-in-card">
        <span className="board-tile large" aria-hidden="true">
          <CardIcon size={28} />
        </span>
        {preview === undefined ? (
          <p className="status-inline">Loading…</p>
        ) : preview === null ? (
          <>
            <h1>Link expired</h1>
            <p>This link was used already or is more than 10 minutes old. Use Save to PrikBoard in Discord again for a new one.</p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Go to your boards
              </button>
            </div>
          </>
        ) : linked ? (
          <>
            <h1>Discord account linked</h1>
            <p>
              Go back to Discord and use Save to PrikBoard again. You can unlink it any time under Account, Discord.
            </p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Done
              </button>
            </div>
          </>
        ) : (
          <>
            <h1>Link your Discord account?</h1>
            <p>
              The Discord account <strong>{preview.discordName}</strong> will act as <strong>{user.displayName}</strong> when it uses
              PrikBoard's Discord bot: messages it saves become cards made by you, on boards you can edit.
            </p>
            <p className="sign-in-error">
              Only continue if you are {preview.discordName} in Discord and just clicked Connect there. If someone sent you this link,
              cancel.
            </p>
            {error && (
              <p className="sign-in-error" role="alert">
                {error}
              </p>
            )}
            <div className="sign-in-providers">
              <button type="button" className="primary" disabled={busy} onClick={() => void link()}>
                Link
              </button>
              <button type="button" className="ghost" disabled={busy} onClick={onDone}>
                Cancel
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
