import { useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api/client'
import type { AccessToken, BoardRole, BoardSummary, CreatedAccessToken } from '../api/types'
import { SparkleIcon, UserIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Select, type SelectOption } from './Select'

const accessOptions: SelectOption<BoardRole>[] = [
  { value: 'Editor', label: 'Can edit', description: 'Change cards, buckets and images (never members or boards)' },
  { value: 'Viewer', label: 'Read only', description: 'Look, but not change anything' },
]

const expiryOptions: SelectOption<string>[] = [
  { value: '30', label: '30 days' },
  { value: '90', label: '90 days' },
  { value: '180', label: '180 days' },
  { value: '365', label: '1 year' },
]

const describeAccess = (token: AccessToken, boards: BoardSummary[]) => {
  if (token.kind === 'App') return 'Acts as you · signed in from VS Code'
  const what = token.access === 'Editor' ? 'Can edit' : 'Read only'
  if (!token.boardIds) return `${what} · all your boards`
  const names = token.boardIds.map((id) => boards.find((b) => b.id === id)?.name ?? 'a board you left')
  return `${what} · ${names.join(', ')}`
}

const date = (iso: string) => new Date(iso).toLocaleDateString()

/**
 * Personal access tokens for agents and scripts (Claude Code, Cursor, curl). Each acts as its own agent on your boards,
 * with your role capped at the access chosen here; the token itself is shown once.
 */
export function AccessTokens({ boards }: { boards: BoardSummary[] }) {
  const [tokens, setTokens] = useState<AccessToken[] | null>(null)
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState<CreatedAccessToken | null>(null)
  const [revoking, setRevoking] = useState<AccessToken | null>(null)
  const [error, setError] = useState<string | null>(null)
  // When the dialog opened: good enough to tell expired tokens apart.
  const [now] = useState(() => Date.now())

  useEffect(() => {
    api.listAccessTokens().then(setTokens, (e) => setError(errorMessage(e)))
  }, [])

  async function revoke(token: AccessToken) {
    setRevoking(null)
    try {
      await api.revokeAccessToken(token.id)
      setTokens((current) => current?.filter((t) => t.id !== token.id) ?? null)
      if (created?.details.id === token.id) setCreated(null)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <section className="dialog-section">
      <h3>Agents and access tokens</h3>
      <p className="dialog-hint">
        Let Claude Code, Cursor or a script work on your boards through the API or MCP. Changes show as the agent, and it can
        never do more than you can.
      </p>

      {created && (
        <NewToken
          created={created}
          onDone={() => {
            setCreated(null)
            // It may have been used already (pasted straight into an agent).
            api.listAccessTokens().then(setTokens, (e) => setError(errorMessage(e)))
          }}
        />
      )}

      {tokens && tokens.length > 0 && (
        <ul className="row-list">
          {tokens.map((token) => (
            <li key={token.id}>
              <span className={token.kind === 'App' ? 'avatar avatar-md' : 'avatar avatar-md agent'} aria-hidden="true">
                {token.kind === 'App' ? <UserIcon size={13} /> : <SparkleIcon size={13} />}
              </span>
              <span className="row-main token-row">
                <span>
                  {token.name} <span className="token-hint">…{token.hint}</span>
                </span>
                <span className="row-meta">{describeAccess(token, boards)}</span>
                <span className="row-meta">
                  {Date.parse(token.expiresAt) <= now ? 'Expired' : `Expires ${date(token.expiresAt)}`} ·{' '}
                  {token.lastUsedAt ? `last used ${date(token.lastUsedAt)}` : 'never used'}
                </span>
              </span>
              <button type="button" className="ghost small" onClick={() => setRevoking(token)}>
                Revoke
              </button>
            </li>
          ))}
        </ul>
      )}

      {creating ? (
        <TokenForm
          boards={boards}
          onCancel={() => setCreating(false)}
          onCreated={(token) => {
            setCreating(false)
            setCreated(token)
            setTokens((current) => [token.details, ...(current ?? [])])
          }}
        />
      ) : (
        <div>
          <button type="button" className="ghost small" onClick={() => setCreating(true)}>
            New token
          </button>
        </div>
      )}
      {error && <p className="widget-error">{error}</p>}

      {revoking && (
        <ConfirmDialog
          title={`Revoke "${revoking.name}"?`}
          message={
            revoking.kind === 'App'
              ? 'The app is signed out at once. What you did with it stays on the boards.'
              : 'Anything using this token stops working at once. What its agent already did stays on the boards.'
          }
          confirmLabel="Revoke"
          onConfirm={() => void revoke(revoking)}
          onCancel={() => setRevoking(null)}
        />
      )}
    </section>
  )
}

function TokenForm({ boards, onCreated, onCancel }: { boards: BoardSummary[]; onCreated: (token: CreatedAccessToken) => void; onCancel: () => void }) {
  const [name, setName] = useState('')
  const [access, setAccess] = useState<BoardRole>('Editor')
  const [expiry, setExpiry] = useState('90')
  const [allBoards, setAllBoards] = useState(true)
  const [chosen, setChosen] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const valid = name.trim() !== '' && (allBoards || chosen.length > 0)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      onCreated(
        await api.createAccessToken({
          name: name.trim(),
          access,
          boardIds: allBoards ? undefined : chosen,
          expiresInDays: Number(expiry),
        }),
      )
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <form className="token-form" onSubmit={submit}>
      <input autoFocus aria-label="Token name" placeholder="What is it for? For example “Claude Code laptop”" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      <div className="inline-form">
        <Select label="Access" value={access} options={accessOptions} onChange={setAccess} />
        <Select label="Expires after" value={expiry} options={expiryOptions} onChange={setExpiry} />
      </div>
      <fieldset className="token-boards">
        <legend className="dialog-hint">Boards</legend>
        <label>
          <input type="radio" name="token-boards" checked={allBoards} onChange={() => setAllBoards(true)} /> All my boards, including new ones
        </label>
        <label>
          <input type="radio" name="token-boards" checked={!allBoards} onChange={() => setAllBoards(false)} /> Only these:
        </label>
        {!allBoards && (
          <div className="token-board-list">
            {boards.map((board) => (
              <label key={board.id}>
                <input
                  type="checkbox"
                  checked={chosen.includes(board.id)}
                  onChange={(e) => setChosen((current) => (e.target.checked ? [...current, board.id] : current.filter((id) => id !== board.id)))}
                />{' '}
                {board.name}
              </label>
            ))}
          </div>
        )}
      </fieldset>
      {error && <p className="widget-error">{error}</p>}
      <div className="dialog-actions">
        <button type="button" className="ghost" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="primary" disabled={!valid}>
          Create token
        </button>
      </div>
    </form>
  )
}

/** The token, once, with ready-to-paste setup for an MCP client and for the REST API. */
function NewToken({ created, onDone }: { created: CreatedAccessToken; onDone: () => void }) {
  const [copied, setCopied] = useState<string | null>(null)
  const origin = window.location.origin
  const mcp = `claude mcp add --transport http prikboard ${origin}/mcp --header "Authorization: Bearer ${created.token}"`
  const curl = `curl -H "Authorization: Bearer ${created.token}" ${origin}/api/boards`

  async function copy(what: string, text: string) {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(what)
    } catch {
      setCopied(null)
    }
  }

  const field = (what: string, label: string, text: string) => (
    <div className="copy-field">
      <input readOnly aria-label={label} value={text} onFocus={(e) => e.target.select()} />
      <button type="button" className="ghost small" onClick={() => void copy(what, text)}>
        {copied === what ? 'Copied' : 'Copy'}
      </button>
    </div>
  )

  return (
    <div className="token-created" role="status">
      <p className="dialog-text">
        <strong>Copy “{created.details.name}” now.</strong> You won’t see it again.
      </p>
      {field('token', 'Access token', created.token)}
      <p className="dialog-hint">Claude Code (MCP):</p>
      {field('mcp', 'Claude Code command', mcp)}
      <p className="dialog-hint">REST API:</p>
      {field('curl', 'curl example', curl)}
      <div className="dialog-actions">
        <button type="button" className="primary" onClick={onDone}>
          Done
        </button>
      </div>
    </div>
  )
}
