import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardInvite, BoardMember, BoardRole, BoardSummary, CreatedInvite, TokenIssuer, UserMatch } from '../api/types'
import { SparkleIcon } from '../board/icons'
import { Dialog } from './Dialog'
import { initials } from './initials'
import { RoleSelect } from './RoleSelect'
import { Select } from './Select'

interface ShareDialogProps {
  board: BoardSummary
  onClose: () => void
  /** You removed yourself from the board. */
  onLeft: () => void
}

/** Who is on the board, and (for owners) adding people by exact email, invite links and agents. */
export function ShareDialog({ board, onClose, onLeft }: ShareDialogProps) {
  const owner = board.yourRole === 'Owner'
  const [members, setMembers] = useState<BoardMember[] | null>(null)
  const [invites, setInvites] = useState<BoardInvite[]>([])
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      setMembers(await api.listBoardMembers(board.id))
      if (owner) setInvites(await api.listBoardInvites(board.id))
    } catch (e) {
      setError(errorMessage(e))
    }
  }, [board.id, owner])

  useEffect(() => {
    api.listBoardMembers(board.id).then(setMembers, (e) => setError(errorMessage(e)))
    if (owner) api.listBoardInvites(board.id).then(setInvites, (e) => setError(errorMessage(e)))
  }, [board.id, owner])

  /** Runs a change and reloads; errors (such as "last owner") show in the dialog. */
  async function attempt(action: () => Promise<unknown>) {
    setError(null)
    try {
      await action()
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    } finally {
      await reload()
    }
  }

  async function remove(member: BoardMember) {
    if (await attempt(() => api.removeBoardMember(board.id, member.principalId)) && member.isYou) onLeft()
  }

  return (
    <Dialog title="Share board" subtitle={board.name} onClose={onClose} wide>
      <div className="dialog-body">
        <section className="dialog-section">
          <h3>People and agents</h3>
          {members === null ? (
            <p className="dialog-hint">Loading…</p>
          ) : (
            <ul className="row-list">
              {members.map((m) => (
                <li key={m.principalId}>
                  <span className={`avatar avatar-md${m.kind === 'Agent' ? ' agent' : ''}`} aria-hidden="true">
                    {m.kind === 'Agent' ? <SparkleIcon size={13} /> : initials(m.displayName)}
                  </span>
                  <span className="row-main">
                    {m.displayName}
                    {m.isYou && <span className="badge">you</span>}
                    {m.kind === 'Agent' && <span className="badge agent" title={`${m.issuer} · ${m.clientId}`}>agent</span>}
                  </span>
                  {owner ? (
                    <RoleSelect
                      label={`Role of ${m.displayName}`}
                      value={m.role}
                      onChange={(role) => void attempt(() => api.updateBoardMember(board.id, m.principalId, role))}
                    />
                  ) : (
                    <span className="row-meta">{m.role}</span>
                  )}
                  {(owner || m.isYou) && (
                    <button type="button" className="ghost small" onClick={() => void remove(m)}>
                      {m.isYou ? 'Leave' : 'Remove'}
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>

        {owner && (
          <>
            <AddPerson boardId={board.id} onAdd={(id, role) => attempt(() => api.addBoardMember(board.id, id, role))} />
            <InviteLinks boardId={board.id} invites={invites} attempt={attempt} />
            <AddAgent boardId={board.id} attempt={attempt} />
          </>
        )}
        {error && <p className="widget-error" role="alert">{error}</p>}
      </div>
    </Dialog>
  )
}

function AddPerson({ boardId, onAdd }: { boardId: string; onAdd: (principalId: string, role: BoardRole) => Promise<boolean> }) {
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<BoardRole>('Editor')
  const [matches, setMatches] = useState<UserMatch[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function find(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      setMatches(await api.findUserByEmail(boardId, email.trim()))
    } catch (e) {
      setMatches(null)
      setError(errorMessage(e))
    }
  }

  return (
    <section className="dialog-section">
      <h3>Add someone who already uses PrikBoard</h3>
      <form className="inline-form" onSubmit={find}>
        <input
          type="email"
          aria-label="Email address"
          placeholder="Their exact email address"
          value={email}
          onChange={(e) => {
            setEmail(e.target.value)
            setMatches(null)
          }}
        />
        <RoleSelect label="Role for the person" value={role} onChange={setRole} />
        <button type="submit" className="primary" disabled={!email.includes('@')}>
          Find
        </button>
      </form>
      <p className="dialog-hint">Only an exact, verified address matches; PrikBoard stores no emails, only fingerprints.</p>
      {error && <p className="widget-error">{error}</p>}
      {matches?.length === 0 && <p className="dialog-hint">No account with that address. Create an invite link below instead.</p>}
      {matches && matches.length > 0 && (
        <ul className="row-list">
          {matches.map((m) => (
            <li key={m.principalId}>
              <span className="avatar avatar-md" aria-hidden="true">
                {initials(m.displayName)}
              </span>
              <span className="row-main">{m.displayName}</span>
              {m.role ? (
                <span className="row-meta">Already {m.role.toLowerCase()}</span>
              ) : (
                <button
                  type="button"
                  className="primary small"
                  onClick={async () => {
                    if (await onAdd(m.principalId, role)) {
                      setEmail('')
                      setMatches(null)
                    }
                  }}
                >
                  Add as {role.toLowerCase()}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function InviteLinks({ boardId, invites, attempt }: { boardId: string; invites: BoardInvite[]; attempt: (action: () => Promise<unknown>) => Promise<boolean> }) {
  const [role, setRole] = useState<BoardRole>('Editor')
  const [created, setCreated] = useState<CreatedInvite | null>(null)
  const [copied, setCopied] = useState(false)
  const link = created ? `${window.location.origin}${created.path}` : ''

  async function create() {
    setCopied(false)
    await attempt(async () => setCreated(await api.createBoardInvite(boardId, role)))
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(link)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <section className="dialog-section">
      <h3>Invite link</h3>
      <div className="inline-form">
        <RoleSelect label="Role for the invite" value={role} onChange={setRole} />
        <button type="button" className="primary" onClick={() => void create()}>
          Create link
        </button>
      </div>
      <p className="dialog-hint">Works once, for whoever opens it first and signs in, within 7 days.</p>
      {created && (
        <div className="copy-field">
          <input readOnly aria-label="Invite link" value={link} onFocus={(e) => e.target.select()} />
          <button type="button" className="ghost small" onClick={() => void copy()}>
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
      )}
      {invites.length > 0 && (
        <ul className="row-list">
          {invites.map((invite) => (
            <li key={invite.id}>
              <span className="row-main">{invite.role} link</span>
              <span className="row-meta">expires {new Date(invite.expiresAt).toLocaleDateString()}</span>
              <button type="button" className="ghost small" onClick={() => void attempt(() => api.revokeBoardInvite(boardId, invite.id))}>
                Revoke
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function AddAgent({ boardId, attempt }: { boardId: string; attempt: (action: () => Promise<unknown>) => Promise<boolean> }) {
  const [issuers, setIssuers] = useState<TokenIssuer[] | null>(null)
  const [issuer, setIssuer] = useState('')
  const [clientId, setClientId] = useState('')
  const [name, setName] = useState('')
  const [role, setRole] = useState<BoardRole>('Editor')

  useEffect(() => {
    api.listTokenIssuers().then(
      (list) => {
        setIssuers(list)
        setIssuer(list[0]?.id ?? '')
      },
      () => setIssuers([]),
    )
  }, [])

  async function add(event: FormEvent) {
    event.preventDefault()
    const ok = await attempt(() =>
      api.addBoardAgent(boardId, { issuer, clientId: clientId.trim(), role, displayName: name.trim() || undefined }),
    )
    if (ok) {
      setClientId('')
      setName('')
    }
  }

  if (issuers === null) return null
  return (
    <section className="dialog-section">
      <h3>Add an agent</h3>
      {issuers.length === 0 ? (
        <p className="dialog-hint">No token issuers are configured, so no agents can sign in yet (see docs/authentication.md).</p>
      ) : (
        <form className="agent-form" onSubmit={add}>
          <Select label="Token issuer" value={issuer} options={issuers.map((i) => ({ value: i.id, label: i.id }))} onChange={setIssuer} />
          <input aria-label="Client id" placeholder="Client id" value={clientId} maxLength={255} onChange={(e) => setClientId(e.target.value)} />
          <input aria-label="Agent name" placeholder="Name (optional)" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
          <RoleSelect label="Role for the agent" value={role} onChange={setRole} />
          <button type="submit" className="primary" disabled={!clientId.trim()}>
            Add agent
          </button>
        </form>
      )}
      <p className="dialog-hint">An agent can call whoAmI with its token to learn its issuer and client id.</p>
    </section>
  )
}
