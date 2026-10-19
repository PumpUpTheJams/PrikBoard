import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardInvite, BoardMember, BoardRole, BoardSummary, CreatedInvite } from '../api/types'
import { SparkleIcon } from '../board/icons'
import { Dialog } from './Dialog'
import { initials } from './initials'
import { RoleSelect } from './RoleSelect'

interface ShareDialogProps {
  board: BoardSummary
  onClose: () => void
  /** You removed yourself from the board. */
  onLeft: () => void
}

/**
 * Who is on the board, and (for owners) invite links. Each member's role and button sit in fixed columns, so they line up
 * whatever the names. Agents and people found by email are added through the API (addBoardAgent, findUserByEmail).
 */
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
                  <span className="member-actions">
                    {owner ? (
                      <RoleSelect
                        label={`Role of ${m.displayName}`}
                        value={m.role}
                        onChange={(role) => void attempt(() => api.updateBoardMember(board.id, m.principalId, role))}
                      />
                    ) : (
                      <span className="row-meta">{m.role}</span>
                    )}
                    {owner || m.isYou ? (
                      <button type="button" className="ghost small" onClick={() => void remove(m)}>
                        {m.isYou ? 'Leave' : 'Remove'}
                      </button>
                    ) : (
                      <span />
                    )}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>

        {owner && <InviteLinks boardId={board.id} invites={invites} attempt={attempt} />}
        {error && <p className="widget-error" role="alert">{error}</p>}
      </div>
    </Dialog>
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
