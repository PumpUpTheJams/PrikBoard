import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { api, errorMessage } from '../api/client'
import { boardRoles, type AdminBoard, type AdminBoardMember, type AdminUser, type BoardRole, type Point } from '../api/types'
import { isDrag } from '../board/gesture'
import { CloseIcon, PeopleIcon, SearchIcon, ShieldIcon, SparkleIcon, TrashIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Face } from './Avatar'
import { plural } from './plural'
import { useAttempt } from './useAttempt'

interface AdminPageProps {
  /** Your own admin rights changed (you handed them over), so the caller needs reloading. */
  onYouChanged: () => void
  onBack: () => void
}

/** Where a dragged person would land: the Admins bucket, or a board (and one of its role zones). */
type DropTarget = { kind: 'admin' } | { kind: 'board'; boardId: string; role: BoardRole | null }

interface PersonDrag {
  user: AdminUser
  start: Point
  current: Point
  active: boolean
  target: DropTarget | null
}

type Pending =
  | { kind: 'deleteUser'; user: AdminUser }
  | { kind: 'deleteBoard'; board: AdminBoard }
  | { kind: 'removeMember'; board: AdminBoard; member: AdminBoardMember }
  | { kind: 'removeAdmin'; user: AdminUser }

/** Space a board row keeps for its name above the role zones, and their inset (match .admin-zones). */
const ZONES_TOP = 40
const ZONES_INSET = 8
/** Member chips a board row shows before "+N". */
const CHIPS_SHOWN = 5
/** How close (px) to a list's top or bottom edge a drag starts scrolling it, and its fastest step per frame. */
const EDGE = 56
const MAX_STEP = 18
/** The share of MAX_STEP a list scrolls by with the pointer on its edge; it speeds up further past it. */
const STEP_AT_EDGE = 0.6

/**
 * The drop target under the pointer, found through the data-drop attributes. A board's role comes from where the
 * pointer is on its row (the zones split it in three), so it doesn't depend on the zones having rendered yet.
 */
function targetAt(point: Point): DropTarget | null {
  const zone = document.elementFromPoint(point.x, point.y)?.closest<HTMLElement>('[data-drop]')
  if (!zone) return null
  if (zone.dataset.drop === 'admin') return { kind: 'admin' }
  const row = zone.closest<HTMLElement>('[data-drop="board"]')
  const boardId = row?.dataset.board
  if (!row || !boardId) return null

  const rect = row.getBoundingClientRect()
  if (point.y < rect.top + ZONES_TOP) return { kind: 'board', boardId, role: null }
  const third = (rect.width - 2 * ZONES_INSET) / boardRoles.length
  const index = Math.floor((point.x - rect.left - ZONES_INSET) / third)
  return { kind: 'board', boardId, role: boardRoles[Math.min(Math.max(index, 0), boardRoles.length - 1)] }
}

/** How far to scroll a list this frame: faster the closer the pointer is to (or the further past) its edge. */
function edgeStep(y: number, top: number, bottom: number) {
  if (y < top + EDGE) return -Math.min(MAX_STEP, Math.ceil(((top + EDGE - y) / EDGE) * MAX_STEP * STEP_AT_EDGE))
  if (y > bottom - EDGE) return Math.min(MAX_STEP, Math.ceil(((y - bottom + EDGE) / EDGE) * MAX_STEP * STEP_AT_EDGE))
  return 0
}

const matches = (text: string, query: string) => text.toLocaleLowerCase().includes(query)

const loadAll = () => Promise.all([api.adminListUsers(), api.adminListBoards()])

const seen = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })

/** What the confirmation for a pending change says. */
function confirmation(pending: Pending): { title: string; message: string; confirmLabel: string } {
  switch (pending.kind) {
    case 'deleteUser':
      return {
        title: `Delete ${pending.user.displayName}?`,
        message:
          'Their account, sign-ins, board memberships and access tokens are deleted, and they are signed out. Cards they wrote stay. They can only come back through a new invite link.',
        confirmLabel: 'Delete user',
      }
    case 'deleteBoard':
      return {
        title: `Delete "${pending.board.name}"?`,
        message: `The board and all of its buckets, cards and images are deleted for its ${plural(pending.board.members.length, 'member', 'members')}. This can't be undone.`,
        confirmLabel: 'Delete board',
      }
    case 'removeMember':
      return {
        title: `Take ${pending.member.displayName} off this board?`,
        message: `They lose access to "${pending.board.name}" until someone adds them again.`,
        confirmLabel: 'Remove',
      }
    case 'removeAdmin':
      return {
        title: pending.user.isYou ? 'Stop being an admin?' : `Stop ${pending.user.displayName} being an admin?`,
        message: pending.user.isYou
          ? "You won't see this page any more. Another admin can make you one again."
          : 'They keep their boards, but can no longer see this page.',
        confirmLabel: 'No longer admin',
      }
  }
}

/**
 * Running the instance: everyone with an account on the left, the Admins bucket and every board on the right, each in a
 * list of its own that scrolls and can be searched. Drag a person onto a board's role zone to give them that role there,
 * or onto Admins to make them an admin; a list scrolls by itself while you drag near its top or bottom.
 */
export function AdminPage({ onYouChanged, onBack }: AdminPageProps) {
  const [users, setUsers] = useState<AdminUser[] | null>(null)
  const [boards, setBoards] = useState<AdminBoard[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [drag, setDrag] = useState<PersonDrag | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [peopleQuery, setPeopleQuery] = useState('')
  const [boardQuery, setBoardQuery] = useState('')
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set())
  const pointer = useRef<Point | null>(null)

  const reload = useCallback(
    () =>
      loadAll().then(
        ([u, b]) => {
          setUsers(u)
          setBoards(b)
        },
        (e) => setError(errorMessage(e)),
      ),
    [],
  )

  useEffect(() => {
    void reload()
    // Others change boards and memberships too; catch up when you come back to the tab.
    const onFocus = () => void reload()
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [reload])

  // While a person is dragged near the top or bottom of a list, scroll that list, so far-away boards can be reached.
  const dragging = drag?.active ?? false
  useEffect(() => {
    if (!dragging) return
    let frame = requestAnimationFrame(function tick() {
      const p = pointer.current
      if (p) {
        for (const list of document.querySelectorAll<HTMLElement>('[data-scroll]')) {
          const rect = list.getBoundingClientRect()
          if (p.x < rect.left || p.x > rect.right || p.y < rect.top - EDGE || p.y > rect.bottom + EDGE) continue
          const before = list.scrollTop
          list.scrollTop += edgeStep(p.y, rect.top, rect.bottom)
          // What is under the pointer moved with the list.
          if (list.scrollTop !== before) setDrag((d) => d && { ...d, target: targetAt(p) })
        }
      }
      frame = requestAnimationFrame(tick)
    })
    return () => cancelAnimationFrame(frame)
  }, [dragging])

  /** Errors show at the top. */
  const attempt = useAttempt(reload, setError)

  function drop(user: AdminUser, target: DropTarget) {
    if (target.kind === 'admin') {
      if (!user.isAdmin) void attempt(() => api.adminUpdateUser(user.id, { isAdmin: true }))
      return
    }
    const { boardId, role } = target
    if (!role) return
    const current = boards?.find((b) => b.id === boardId)?.members.find((m) => m.principalId === user.id)
    if (current?.role === role) return
    void attempt(() => api.adminSetBoardMember(boardId, user.id, role))
  }

  function onPersonDown(e: ReactPointerEvent<HTMLLIElement>, user: AdminUser) {
    if (e.button !== 0 || (e.target as Element).closest('button')) return
    // No text selection while dragging a person across the page.
    e.preventDefault()
    e.currentTarget.setPointerCapture(e.pointerId)
    const point = { x: e.clientX, y: e.clientY }
    pointer.current = point
    setDrag({ user, start: point, current: point, active: false, target: null })
  }

  function onPersonMove(e: ReactPointerEvent<HTMLLIElement>) {
    if (!drag) return
    const current = { x: e.clientX, y: e.clientY }
    pointer.current = current
    const active = drag.active || isDrag({ start: drag.start, current })
    setDrag({ ...drag, current, active, target: active ? targetAt(current) : null })
  }

  function onPersonUp(e: ReactPointerEvent<HTMLLIElement>) {
    const target = drag?.active ? targetAt({ x: e.clientX, y: e.clientY }) : null
    if (drag && target) drop(drag.user, target)
    endDrag()
  }

  function endDrag() {
    pointer.current = null
    setDrag(null)
  }

  function confirmPending(action: Pending) {
    setPending(null)
    switch (action.kind) {
      case 'deleteUser':
        void attempt(() => api.adminDeleteUser(action.user.id))
        return
      case 'deleteBoard':
        void attempt(() => api.adminDeleteBoard(action.board.id))
        return
      case 'removeMember':
        void attempt(() => api.adminRemoveBoardMember(action.board.id, action.member.principalId))
        return
      case 'removeAdmin':
        void attempt(() => api.adminUpdateUser(action.user.id, { isAdmin: false })).then((ok) => {
          if (ok && action.user.isYou) onYouChanged()
        })
    }
  }

  function askDelete(user: AdminUser) {
    if (user.soleOwnerOf.length > 0) {
      setError(
        `${user.displayName} is the only owner of ${user.soleOwnerOf.map((b) => `"${b.name}"`).join(', ')}. ` +
          'Drag someone else onto Owner there first.',
      )
      return
    }
    setPending({ kind: 'deleteUser', user })
  }

  function toggleExpanded(boardId: string) {
    setExpanded((current) => {
      const next = new Set(current)
      if (!next.delete(boardId)) next.add(boardId)
      return next
    })
  }

  const admins = users?.filter((u) => u.isAdmin) ?? []
  const target = drag?.active ? drag.target : null
  const hoveredBoard = target?.kind === 'board' ? target.boardId : null
  const people = peopleQuery.trim().toLocaleLowerCase()
  const shownUsers = users?.filter((u) => matches(u.displayName, people)) ?? []
  const boardSearch = boardQuery.trim().toLocaleLowerCase()
  const shownBoards =
    boards?.filter((b) => matches(b.name, boardSearch) || b.members.some((m) => matches(m.displayName, boardSearch))) ?? []

  return (
    <div className={`admin-page${drag?.active ? ' dragging' : ''}`}>
      <header className="admin-head">
        <div>
          <h2>Admin</h2>
          <p>Drag a person onto a board and drop them on a role, or onto Admins. Admins see who is on a board, not what is on it.</p>
        </div>
        <button type="button" className="ghost small" onClick={onBack}>
          Back to boards
        </button>
      </header>

      {error && (
        <p className="widget-error admin-error" role="alert">
          {error}
        </p>
      )}

      {users === null || boards === null ? (
        <p className="status">Loading…</p>
      ) : (
        <div className="admin-columns">
          <section className="admin-panel" aria-labelledby="admin-people">
            <h3 id="admin-people">
              People <span className="row-meta">{users.length}</span>
            </h3>
            <label className="admin-search">
              <SearchIcon />
              <input
                type="search"
                placeholder="Search people"
                aria-label="Search people"
                value={peopleQuery}
                onChange={(e) => setPeopleQuery(e.target.value)}
              />
            </label>
            <ul className="row-list admin-people admin-scroll" data-scroll>
              {shownUsers.map((u) => (
                <PersonRow
                  key={u.id}
                  user={u}
                  lifted={!!drag?.active && drag.user.id === u.id}
                  onPointerDown={(e) => onPersonDown(e, u)}
                  onPointerMove={onPersonMove}
                  onPointerUp={onPersonUp}
                  onLostPointerCapture={endDrag}
                  onDelete={() => askDelete(u)}
                />
              ))}
              {shownUsers.length === 0 && <li className="admin-none">Nobody matches "{peopleQuery.trim()}".</li>}
            </ul>
          </section>

          <section className="admin-panel" aria-labelledby="admin-boards">
            <div className={`admin-bucket admin-admins${target?.kind === 'admin' ? ' drop-target' : ''}`} data-drop="admin">
              <h3>
                <ShieldIcon size={14} /> Admins
              </h3>
              <div className="admin-chips">
                {admins.map((u) => (
                  <span key={u.id} className="admin-chip">
                    {u.displayName}
                    {admins.length > 1 && (
                      <button type="button" aria-label={`Stop ${u.displayName} being an admin`} title="No longer admin" onClick={() => setPending({ kind: 'removeAdmin', user: u })}>
                        <CloseIcon size={12} />
                      </button>
                    )}
                  </span>
                ))}
              </div>
            </div>

            <h3 id="admin-boards">
              Boards <span className="row-meta">{boards.length}</span>
            </h3>
            <label className="admin-search">
              <SearchIcon />
              <input
                type="search"
                placeholder="Search boards or members"
                aria-label="Search boards or members"
                value={boardQuery}
                onChange={(e) => setBoardQuery(e.target.value)}
              />
            </label>
            <div className="admin-board-list admin-scroll" data-scroll>
              {shownBoards.map((b) => (
                <BoardRow
                  key={b.id}
                  board={b}
                  open={expanded.has(b.id)}
                  hovered={hoveredBoard === b.id}
                  dropRole={target?.kind === 'board' ? target.role : null}
                  onToggle={() => toggleExpanded(b.id)}
                  onDelete={() => setPending({ kind: 'deleteBoard', board: b })}
                  onRemove={(member) => setPending({ kind: 'removeMember', board: b, member })}
                />
              ))}
              {shownBoards.length === 0 && <p className="admin-none">No board matches "{boardQuery.trim()}".</p>}
            </div>
          </section>
        </div>
      )}

      {drag?.active && (
        <div className="drag-ghost admin-ghost" style={{ left: drag.current.x + 12, top: drag.current.y + 8 }}>
          <span className="avatar avatar-sm" aria-hidden="true">
            <Face name={drag.user.displayName} avatarUrl={drag.user.avatarUrl} />
          </span>
          {drag.user.displayName}
        </div>
      )}

      {pending && <ConfirmDialog key={pending.kind} {...confirmation(pending)} onConfirm={() => confirmPending(pending)} onCancel={() => setPending(null)} />}
    </div>
  )
}

interface PersonRowProps {
  user: AdminUser
  /** Being dragged. */
  lifted: boolean
  onPointerDown: (e: ReactPointerEvent<HTMLLIElement>) => void
  onPointerMove: (e: ReactPointerEvent<HTMLLIElement>) => void
  onPointerUp: (e: ReactPointerEvent<HTMLLIElement>) => void
  onLostPointerCapture: () => void
  onDelete: () => void
}

/** Someone with an account, in the people list: dragged onto a board or onto Admins. */
function PersonRow({ user, lifted, onPointerDown, onPointerMove, onPointerUp, onLostPointerCapture, onDelete }: PersonRowProps) {
  return (
    <li
      className={lifted ? 'lifted' : undefined}
      title="Drag onto a board or onto Admins"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onLostPointerCapture={onLostPointerCapture}
    >
      <span className="avatar avatar-md" aria-hidden="true">
        <Face name={user.displayName} avatarUrl={user.avatarUrl} />
      </span>
      <span className="row-main">
        {user.displayName}
        {user.isYou && <span className="badge">you</span>}
        {user.isAdmin && <span className="badge admin">admin</span>}
      </span>
      <span className="row-meta" title={`Joined ${seen(user.createdAt)}`}>
        {plural(user.boardCount, 'board', 'boards')} · seen {seen(user.lastSeenAt)}
      </span>
      {!user.isYou && (
        <button type="button" className="icon-button" aria-label={`Delete ${user.displayName}`} title="Delete user" onClick={onDelete}>
          <TrashIcon />
        </button>
      )}
    </li>
  )
}

interface BoardRowProps {
  board: AdminBoard
  /** Shows every member, not only the first few. */
  open: boolean
  /** A person is dragged over it: its role zones show. */
  hovered: boolean
  /** The role zone under the pointer, if any. */
  dropRole: BoardRole | null
  onToggle: () => void
  onDelete: () => void
  onRemove: (member: AdminBoardMember) => void
}

/** A board in the boards list, with its members as chips; a person dropped on one of its role zones gets that role. */
function BoardRow({ board, open, hovered, dropRole, onToggle, onDelete, onRemove }: BoardRowProps) {
  const chips = open ? board.members : board.members.slice(0, CHIPS_SHOWN)
  const hidden = board.members.length - chips.length
  return (
    <article className={`admin-bucket admin-board${hovered ? ' drop-target' : ''}`} data-drop="board" data-board={board.id}>
      <header>
        <span className="admin-count" title={plural(board.members.length, 'member', 'members')}>
          <PeopleIcon size={12} />
          {board.members.length}
        </span>
        <span className="admin-board-name">{board.name}</span>
        <button type="button" className="icon-button admin-board-delete" aria-label={`Delete board ${board.name}`} title="Delete board" onClick={onDelete}>
          <TrashIcon size={14} />
        </button>
      </header>
      <div className="admin-chips">
        {chips.map((m) => (
          <span key={m.principalId} className={`admin-chip${m.kind === 'Agent' ? ' agent' : ''}`}>
            {m.kind === 'Agent' && <SparkleIcon size={11} />}
            {m.displayName}
            <span className="admin-chip-role">{m.role}</span>
            <button type="button" aria-label={`Take ${m.displayName} off ${board.name}`} title="Remove from board" onClick={() => onRemove(m)}>
              <CloseIcon size={12} />
            </button>
          </span>
        ))}
        {(hidden > 0 || open) && board.members.length > CHIPS_SHOWN && (
          <button type="button" className="admin-more" aria-expanded={open} onClick={onToggle}>
            {open ? 'Show fewer' : `+${hidden}`}
          </button>
        )}
        {board.members.length === 0 && <span className="row-meta">Nobody</span>}
      </div>
      {hovered && (
        <div className="admin-zones">
          {boardRoles.map((role) => (
            <div key={role} className={`admin-zone${dropRole === role ? ' drop-target' : ''}`}>
              {role}
            </div>
          ))}
        </div>
      )}
    </article>
  )
}
