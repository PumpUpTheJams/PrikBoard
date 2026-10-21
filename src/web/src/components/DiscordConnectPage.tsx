import { useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardSummary, DiscordConnectCode } from '../api/types'
import { CardIcon } from '../board/icons'
import type { DiscordConnectRequest } from './discord'
import { Select, type SelectOption } from './Select'

type Reach = 'channel' | 'server'

const reachOptions: SelectOption<Reach>[] = [
  { value: 'channel', label: 'That channel', description: 'Only where /prikboard connect was run' },
  { value: 'server', label: 'Whole server', description: 'Every channel the bot can read' },
]

interface DiscordConnectPageProps {
  request: DiscordConnectRequest
  onDone: () => void
}

/**
 * Opened from the PrikBoard Discord bot's /prikboard connect: a board owner picks boards whose links the bot may preview
 * in that server, and gets a code for the Discord admin to paste with /prikboard finish. Anything in this link could be
 * forged, so the page names no server or channel: the bot names them when the code is pasted.
 */
export function DiscordConnectPage({ request, onDone }: DiscordConnectPageProps) {
  const [boards, setBoards] = useState<BoardSummary[] | null>(null)
  const [chosen, setChosen] = useState<Record<string, Reach>>({})
  const [code, setCode] = useState<DiscordConnectCode | null>(null)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const complete = request.codeChallenge.length === 43

  useEffect(() => {
    api.listBoards().then(
      (all) => setBoards(all.filter((b) => b.yourRole === 'Owner')),
      (e) => setError(errorMessage(e)),
    )
  }, [])

  function toggle(boardId: string) {
    setChosen((current) => {
      const next = { ...current }
      if (next[boardId]) delete next[boardId]
      else next[boardId] = 'channel'
      return next
    })
  }

  async function connect() {
    setBusy(true)
    setError(null)
    try {
      const picked = Object.entries(chosen).map(([boardId, reach]) => ({ boardId, wholeServer: reach === 'server' }))
      setCode(await api.approveDiscordConnect(request.codeChallenge, picked))
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const command = code ? `/prikboard finish code:${code.code}` : ''

  async function copy() {
    try {
      await navigator.clipboard.writeText(command)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <div className="sign-in">
      <div className="sign-in-card discord-connect">
        <span className="board-tile large" aria-hidden="true">
          <CardIcon size={28} />
        </span>
        {!complete ? (
          <>
            <h1>Connect link incomplete</h1>
            <p>This link is missing parts. Run /prikboard connect in Discord again.</p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Go to your boards
              </button>
            </div>
          </>
        ) : code ? (
          <>
            <h1>Paste this into Discord</h1>
            <p>In the Discord server where /prikboard connect was run, send:</p>
            {/* Wraps instead of scrolling, so the whole code shows: it is too long for one line. */}
            <div className="copy-field discord-command">
              <code aria-label="Discord command">{command}</code>
              <button type="button" className="ghost small" onClick={() => void copy()}>
                {copied ? 'Copied' : 'Copy'}
              </button>
            </div>
            <p className="sign-in-error" role="alert">
              It works once, for 5 minutes, and only in the server that started connecting. The bot then says which server and
              channel are connected.
            </p>
            <div className="sign-in-providers">
              <button type="button" className="ghost" onClick={onDone}>
                Done
              </button>
            </div>
          </>
        ) : (
          <>
            <h1>Connect a Discord server?</h1>
            <p>
              The PrikBoard bot will preview links to the boards you pick in that Discord server: card titles and their first note,
              board names and descriptions. Everyone who can read the channel sees the previews. You can disconnect any time in the
              board's menu, under Discord.
            </p>
            {boards === null ? (
              <p>Loading…</p>
            ) : boards.length === 0 ? (
              <p>You don't own any boards. Only a board's owners can connect it.</p>
            ) : (
              <ul className="discord-connect-boards">
                {boards.map((b) => (
                  <li key={b.id}>
                    <label>
                      <input type="checkbox" checked={b.id in chosen} onChange={() => toggle(b.id)} />
                      <span>{b.name}</span>
                    </label>
                    {chosen[b.id] && (
                      <Select
                        label={`Where to preview ${b.name}`}
                        value={chosen[b.id]}
                        options={reachOptions}
                        onChange={(reach) => setChosen((current) => ({ ...current, [b.id]: reach }))}
                      />
                    )}
                  </li>
                ))}
              </ul>
            )}
            <p className="sign-in-error">
              Only continue if you or an admin of your Discord server just ran /prikboard connect. If someone sent you this link,
              cancel.
            </p>
            {error && (
              <p className="sign-in-error" role="alert">
                {error}
              </p>
            )}
            <div className="sign-in-providers">
              <button
                type="button"
                className="primary"
                disabled={busy || Object.keys(chosen).length === 0}
                onClick={() => void connect()}
              >
                Connect
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
