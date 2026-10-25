import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardInvite, BoardMember, BoardRole, BoardSummary, CreatedInvite } from '../api/types'
import { SparkleIcon } from '../board/icons'
import { useCopy } from '../board/useCopy'
import { CopyField } from './CopyField'
import { Dialog } from './Dialog'
import { Face } from './Avatar'
import { DISCORD_ISSUER, SLACK_ISSUER } from './discord'
import { RoleSelect } from './RoleSelect'
import { useAttempt, type Attempt } from './useAttempt'

interface ShareDialogProps {
  board: BoardSummary
  onClose: () => void
  /** You removed yourself from the board. */
  onLeft: () => void
  /** An owner made the board public or members-only, or changed what its visitors see. */
  onChanged: (board: BoardSummary) => void
}

/**
 * Who is on the board, and (for owners) invite links. Each member's role and button sit in fixed columns, so they line up
 * whatever the names. Agents and people found by email are added through the API (addBoardAgent, findUserByEmail).
 */
export function ShareDialog({ board, onClose, onLeft, onChanged }: ShareDialogProps) {
  const owner = board.yourRole === 'Owner'
  const [members, setMembers] = useState<BoardMember[] | null>(null)
  const [invites, setInvites] = useState<BoardInvite[]>([])
  const [error, setError] = useState<string | null>(null)

  /** Loads the members, then (for owners) the invites. */
  const reload = useCallback(
    () =>
      api
        .listBoardMembers(board.id)
        .then(setMembers)
        .then(() => (owner ? api.listBoardInvites(board.id).then(setInvites) : undefined))
        .catch((e) => setError(errorMessage(e))),
    [board.id, owner],
  )

  useEffect(() => {
    void reload()
  }, [reload])

  /** Errors show in the dialog. */
  const attempt = useAttempt(reload, setError)

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
                    {m.kind === 'Agent' ? <SparkleIcon size={13} /> : <Face name={m.displayName} avatarUrl={m.avatarUrl} />}
                  </span>
                  <span className="row-main">
                    {m.displayName}
                    {m.isYou && <span className="badge">you</span>}
                    {m.issuer === DISCORD_ISSUER ? (
                      <span className="badge discord" title={`Discord server ${m.clientId}: previews this board's links`}>
                        Discord
                      </span>
                    ) : m.issuer === SLACK_ISSUER ? (
                      <span className="badge slack" title={`Slack workspace ${m.clientId}: unfurls this board's links`}>
                        Slack
                      </span>
                    ) : (
                      m.kind === 'Agent' && <span className="badge agent" title={`${m.issuer} · ${m.clientId}`}>agent</span>
                    )}
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
        {owner && <PublicLink board={board} attempt={attempt} onChanged={onChanged} />}
        {owner && <LiveCursors board={board} attempt={attempt} onChanged={onChanged} />}
        {error && <p className="widget-error" role="alert">{error}</p>}
      </div>
    </Dialog>
  )
}

/**
 * For owners: whether anyone with the board's link can view it (read only, signed in or not), and whether those
 * visitors see who is on it or anonymous circles and a count.
 */
function PublicLink({
  board,
  attempt,
  onChanged,
}: {
  board: BoardSummary
  attempt: Attempt
  onChanged: (board: BoardSummary) => void
}) {
  const copier = useCopy()
  const link = `${window.location.origin}/#/boards/${board.shortId}`

  const change = (changes: { isPublic?: boolean; publicShowsPeople?: boolean }) =>
    void attempt(async () => onChanged(await api.updateBoard(board.id, changes)))

  return (
    <section className="dialog-section">
      <h3>Public link</h3>
      <label className="check-row">
        <input type="checkbox" checked={board.isPublic} onChange={(e) => change({ isPublic: e.target.checked })} />
        Anyone with the link can view this board
      </label>
      <label className={`check-row${board.isPublic ? '' : ' disabled'}`}>
        <input
          type="checkbox"
          checked={board.publicShowsPeople}
          disabled={!board.isPublic}
          onChange={(e) => change({ publicShowsPeople: e.target.checked })}
        />
        Show who is on it (names and pictures)
      </label>
      <p className="dialog-hint">
        Visitors don't need an account. They can look, search and open cards, but not change anything or see deleted items.
      </p>
      {board.isPublic && <CopyField label="Public link" value={link} copier={copier} />}
    </section>
  )
}

/** For owners: whether the members looking at the board see each other's pointers, and pins in what they select. */
function LiveCursors({ board, attempt, onChanged }: { board: BoardSummary; attempt: Attempt; onChanged: (board: BoardSummary) => void }) {
  return (
    <section className="dialog-section">
      <h3>Live cursors</h3>
      <label className="check-row">
        <input
          type="checkbox"
          checked={board.showCursors}
          onChange={(e) => {
            const showCursors = e.target.checked
            void attempt(async () => onChanged(await api.updateBoard(board.id, { showCursors })))
          }}
        />
        Show everyone's pointer and selection
      </label>
      <p className="dialog-hint">Members see each other's pointer as their picture, and a pin in whatever they select. Visitors of a public board never see them.</p>
    </section>
  )
}

function InviteLinks({ boardId, invites, attempt }: { boardId: string; invites: BoardInvite[]; attempt: Attempt }) {
  const [role, setRole] = useState<BoardRole>('Editor')
  const [created, setCreated] = useState<CreatedInvite | null>(null)
  const copier = useCopy()
  const link = created ? `${window.location.origin}${created.path}` : ''

  async function create() {
    copier.reset()
    await attempt(async () => setCreated(await api.createBoardInvite(boardId, role)))
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
      {created && <CopyField label="Invite link" value={link} copier={copier} />}
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
