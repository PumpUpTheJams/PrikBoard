import { useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { Caller } from '../api/types'
import { CardIcon } from '../board/icons'
import type { ChatPlatform } from './discord'

interface ChatAccountPageProps {
  platform: ChatPlatform
  code: string
  user: Caller
  onDone: () => void
}

/** Whose chat account a code is for, as either app's preview gives it. */
interface LinkPreview {
  name: string
}

const links: Record<ChatPlatform, { preview: (code: string) => Promise<LinkPreview>; link: (code: string) => Promise<unknown> }> = {
  Discord: {
    preview: (code) => api.previewDiscordAccountLink(code).then((p) => ({ name: p.discordName })),
    link: (code) => api.linkDiscordAccount(code),
  },
  Slack: {
    preview: (code) => api.previewSlackAccountLink(code).then((p) => ({ name: p.slackName })),
    link: (code) => api.linkSlackAccount(code),
  },
}

/**
 * Opened from a chat bot's "Connect your PrikBoard account" link (Discord's button, or the Slack app's modal): links that
 * chat account to you, so Save to PrikBoard there works as you. The page names the account the code is for, so nobody
 * can link theirs to you by sending you their link.
 */
export function ChatAccountPage({ platform, code, user, onDone }: ChatAccountPageProps) {
  // A link without a code has nothing to look up: it shows as expired straight away.
  const [preview, setPreview] = useState<LinkPreview | null | undefined>(code === '' ? null : undefined)
  const [linked, setLinked] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (code !== '') links[platform].preview(code).then(setPreview, () => setPreview(null))
  }, [code, platform])

  async function link() {
    setBusy(true)
    setError(null)
    try {
      await links[platform].link(code)
      setLinked(true)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const bot = platform === 'Slack' ? "PrikBoard's Slack app" : "PrikBoard's Discord bot"
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
            <p>This link was used already or is more than 10 minutes old. Use Save to PrikBoard in {platform} again for a new one.</p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Go to your boards
              </button>
            </div>
          </>
        ) : linked ? (
          <>
            <h1>{platform} account linked</h1>
            <p>
              Go back to {platform} and use Save to PrikBoard again. You can unlink it any time under Account, {platform}.
            </p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Done
              </button>
            </div>
          </>
        ) : (
          <>
            <h1>Link your {platform} account?</h1>
            <p>
              The {platform} account <strong>{preview.name}</strong> will act as <strong>{user.displayName}</strong> when it uses {bot}:
              messages it saves become cards made by you, on boards you can edit.
            </p>
            <p className="sign-in-error">
              Only continue if you are {preview.name} in {platform} and just clicked Connect there. If someone sent you this link,
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
