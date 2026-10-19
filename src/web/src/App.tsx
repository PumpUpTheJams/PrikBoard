import { useCallback, useEffect, useRef, useState } from 'react'
import { api, errorMessage, setUnauthorizedHandler } from './api/client'
import type { Assignee, BoardSummary, Caller } from './api/types'
import { BoardCanvas } from './board/BoardCanvas'
import {
  CheckIcon,
  ChevronDownIcon,
  ExternalIcon,
  PeopleIcon,
  PostItIcon,
  SignOutIcon,
  TrashIcon,
  UserIcon,
} from './board/icons'
import { useBoard } from './board/useBoard'
import { useLive } from './live/useLive'
import { AccountDialog, DisplayNameDialog } from './components/AccountDialogs'
import { AvatarStack } from './components/Avatar'
import { ConfirmDialog } from './components/ConfirmDialog'
import { initials } from './components/initials'
import { InvitePage } from './components/InvitePage'
import { Menu } from './components/Menu'
import { Presence } from './components/Presence'
import { QuickAdd } from './components/QuickAdd'
import { ShareDialog } from './components/ShareDialog'
import { SignIn } from './components/SignIn'

const boardIdFromHash = () => window.location.hash.match(/^#\/boards\/([\w-]+)/)?.[1] ?? null
/** Invite links look like /#/invite/{token}; the token never reaches a server log. */
const inviteFromHash = () => window.location.hash.match(/^#\/invite\/([\w-]+)/)?.[1] ?? null

/** Everyone assigned to something on the board, people first. */
function boardAssignees(notes: { assignees: Assignee[] }[]): Assignee[] {
  const unique = new Map<string, Assignee>()
  for (const a of notes.flatMap((n) => n.assignees)) unique.set(a.principalId, a)
  return [...unique.values()].sort((a, b) => (a.kind === b.kind ? 0 : a.kind === 'Person' ? -1 : 1))
}

export default function App() {
  // undefined while checking the session, null when signed out.
  const [user, setUser] = useState<Caller | null | undefined>(undefined)
  const [invite, setInvite] = useState(inviteFromHash)

  useEffect(() => {
    // Any 401, such as an expired session, drops back to the sign-in screen.
    setUnauthorizedHandler(() => setUser(null))
    api.whoAmI().then(setUser, () => setUser(null))

    const onHashChange = () => setInvite(inviteFromHash())
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  async function signOut() {
    await api.logout().catch(() => {})
    setUser(null)
  }

  if (user === undefined) return <p className="status">Loading…</p>
  // Signing in returns to the same hash, so an invite link survives the round trip.
  if (user === null) return <SignIn />
  if (invite) {
    return (
      <InvitePage
        token={invite}
        onJoined={(boardId) => {
          window.location.hash = `/boards/${boardId}`
        }}
        onDismiss={() => {
          window.location.hash = ''
        }}
      />
    )
  }
  return (
    <>
      <Workspace user={user} onUserChange={setUser} onSignOut={() => void signOut()} />
      {user.kind === 'Session' && user.chosenDisplayName === null && user.suggestedDisplayName !== null && (
        <DisplayNameDialog user={user} onSaved={setUser} />
      )}
    </>
  )
}

interface WorkspaceProps {
  user: Caller
  onUserChange: (user: Caller) => void
  onSignOut: () => void
}

function Workspace({ user, onUserChange, onSignOut }: WorkspaceProps) {
  const [boards, setBoards] = useState<BoardSummary[] | null>(null)
  const [selectedId, setSelectedId] = useState(boardIdFromHash)
  const [error, setError] = useState<string | null>(null)
  const [boardToDelete, setBoardToDelete] = useState<BoardSummary | null>(null)
  const [boardToLeave, setBoardToLeave] = useState<BoardSummary | null>(null)
  const [dialog, setDialog] = useState<'share' | 'account' | null>(null)

  const loadBoards = useCallback(() => api.listBoards().then(setBoards, (e) => setError(errorMessage(e))), [])

  useEffect(() => {
    void loadBoards()

    const onHashChange = () => setSelectedId(boardIdFromHash())
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [loadBoards])

  // Fall back to the newest board when nothing (or something stale) is selected.
  const activeBoard = boards?.find((b) => b.id === selectedId) ?? boards?.[0] ?? null
  const isOwner = activeBoard?.yourRole === 'Owner'

  // The board was deleted or you were removed from it (by an owner, possibly in another tab).
  const onLost = useCallback(() => {
    setError('You no longer have access to that board.')
    setDialog(null)
    window.location.hash = ''
    setSelectedId(null)
    void loadBoards()
  }, [loadBoards])
  // Live updates tell the open board to reload when others save; until connected, the board polls.
  const refreshBoard = useRef(() => {})
  const live = useLive(activeBoard?.id ?? null, { onBoardChanged: () => refreshBoard.current(), onBoardsChanged: loadBoards })
  const { board, run, setPaused, refresh } = useBoard(activeBoard?.id ?? null, setError, onLost, live.connected)
  const { boardLoaded } = live
  useEffect(() => {
    refreshBoard.current = refresh
  }, [refresh])
  // Your name shows on post-its (creator, assignee). Others hear about a rename live; this tab reloads itself.
  const shownName = useRef(user.displayName)
  useEffect(() => {
    if (shownName.current === user.displayName) return
    shownName.current = user.displayName
    refresh()
  }, [user.displayName, refresh])
  // Someone's finished drag stays where they dropped it until the reload shows where it landed.
  useEffect(() => boardLoaded(), [board, boardLoaded])
  const othersHere = live.viewers.filter((v) => v.principalId !== user.id)
  const assignees = board ? boardAssignees(board.notes) : []
  const provider = user.identities.find((i) => i.provider === user.provider)?.providerName ?? user.provider

  function selectBoard(id: string | null) {
    window.location.hash = id ? `/boards/${id}` : ''
    setSelectedId(id)
  }

  async function createBoard(name: string) {
    try {
      const created = await api.createBoard(name)
      setBoards((current) => [created, ...(current ?? [])])
      selectBoard(created.id)
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function renameBoard(target: BoardSummary, name: string) {
    try {
      const renamed = await api.updateBoard(target.id, { name })
      setBoards((current) => current?.map((b) => (b.id === renamed.id ? renamed : b)) ?? null)
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function leaveBoard(target: BoardSummary) {
    setBoardToLeave(null)
    try {
      await api.removeBoardMember(target.id, user.id)
      left(target)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  /** You are no longer on this board. */
  function left(target: BoardSummary) {
    setDialog(null)
    setBoards((current) => current?.filter((b) => b.id !== target.id) ?? null)
    selectBoard(null)
  }

  async function deleteBoard(target: BoardSummary) {
    setBoardToDelete(null)
    try {
      await api.deleteBoard(target.id)
      setBoards((current) => current?.filter((b) => b.id !== target.id) ?? null)
      selectBoard(null)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-start">
          <span className="board-tile" aria-hidden="true">
            <PostItIcon size={16} />
          </span>
          <h1 className="visually-hidden">{activeBoard?.name ?? 'PrikBoard'}</h1>
          <Menu
            label="Boards"
            triggerClassName="board-switcher"
            trigger={
              <>
                <span className="board-name" title={activeBoard?.description ?? undefined}>
                  {activeBoard?.name ?? 'PrikBoard'}
                </span>
                <ChevronDownIcon />
              </>
            }
          >
            {(close) => (
              <>
                {boards && boards.length > 0 && <p className="menu-label">Boards</p>}
                {boards?.map((b) => (
                  <button
                    key={b.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={b.id === activeBoard?.id}
                    className="menu-item"
                    onClick={() => {
                      selectBoard(b.id)
                      close()
                    }}
                  >
                    <span className="menu-item-text">{b.name}</span>
                    {b.id === activeBoard?.id && <CheckIcon />}
                  </button>
                ))}
                <span className="menu-divider" role="separator" />
                <QuickAdd
                  label="New board"
                  placeholder="Project name"
                  onAdd={async (name) => {
                    const ok = await createBoard(name)
                    if (ok) close()
                    return ok
                  }}
                />
                {activeBoard && isOwner && (
                  <QuickAdd label="Rename board" placeholder={activeBoard.name} onAdd={(name) => renameBoard(activeBoard, name)} />
                )}
                {activeBoard && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    onClick={() => {
                      close()
                      setBoardToLeave(activeBoard)
                    }}
                  >
                    <SignOutIcon />
                    Leave board
                  </button>
                )}
                {activeBoard && isOwner && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item danger"
                    onClick={() => {
                      close()
                      setBoardToDelete(activeBoard)
                    }}
                  >
                    <TrashIcon />
                    Delete board
                  </button>
                )}
              </>
            )}
          </Menu>
        </div>

        <div className="topbar-end">
          <Presence viewers={othersHere} />
          {activeBoard && (
            <button type="button" className="share-button" onClick={() => setDialog('share')}>
              <PeopleIcon />
              Share
            </button>
          )}
          {assignees.length > 0 && (
            <div className="topbar-people" aria-label="Assigned on this board">
              <AvatarStack assignees={assignees} max={3} size="md" />
            </div>
          )}
          <span className="topbar-divider" aria-hidden="true" />
          <Menu
            label="Your settings"
            align="end"
            triggerClassName="settings-trigger"
            trigger={
              <>
                <span className="me">{initials(user.displayName)}</span>
                <ChevronDownIcon />
              </>
            }
          >
            {(close) => (
              <>
                <div className="menu-user">
                  <span className="me">{initials(user.displayName)}</span>
                  <span className="menu-user-text">
                    <span className="menu-user-name">{user.displayName}</span>
                    <span className="menu-user-detail">{provider ? `Signed in with ${provider}` : 'Signed in'}</span>
                  </span>
                </div>
                <span className="menu-divider" role="separator" />
                {user.kind === 'Session' && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    onClick={() => {
                      close()
                      setDialog('account')
                    }}
                  >
                    <UserIcon />
                    Account
                  </button>
                )}
                <a role="menuitem" className="menu-item" href="/scalar" target="_blank" rel="noreferrer" onClick={close}>
                  <ExternalIcon />
                  API reference
                </a>
                <a role="menuitem" className="menu-item" href="/openapi/v1.json" target="_blank" rel="noreferrer" onClick={close}>
                  <ExternalIcon />
                  OpenAPI document
                </a>
                <span className="menu-divider" role="separator" />
                <button type="button" role="menuitem" className="menu-item" onClick={onSignOut}>
                  <SignOutIcon />
                  Sign out
                </button>
              </>
            )}
          </Menu>
        </div>
      </header>

      {error && (
        <div className="error" role="alert">
          <span>{error}</span>
          <button type="button" className="ghost" onClick={() => setError(null)}>
            Dismiss
          </button>
        </div>
      )}

      <main>
        {boards === null ? (
          <p className="status">Loading…</p>
        ) : activeBoard ? (
          board ? (
            <BoardCanvas
              key={activeBoard.id}
              board={board}
              run={run}
              setPaused={setPaused}
              remoteDrags={live.drags}
              onDrag={live.sendDrag}
            />
          ) : (
            <p className="status">Loading board…</p>
          )
        ) : (
          <div className="empty">
            <span className="board-tile large" aria-hidden="true">
              <PostItIcon size={28} />
            </span>
            <h2>No boards yet</h2>
            <p>Create a board for your project, then start sticking post-its on it.</p>
            <QuickAdd label="Create a board" placeholder="Project name" onAdd={createBoard} />
          </div>
        )}
      </main>

      {dialog === 'share' && activeBoard && (
        <ShareDialog key={activeBoard.id} board={activeBoard} onClose={() => setDialog(null)} onLeft={() => left(activeBoard)} />
      )}
      {dialog === 'account' && <AccountDialog user={user} boards={boards ?? []} onChange={onUserChange} onClose={() => setDialog(null)} />}

      {boardToLeave && (
        <ConfirmDialog
          title="Leave this board?"
          message={`You'll lose access to "${boardToLeave.name}" until someone adds you again. A board's last owner can't leave.`}
          confirmLabel="Leave board"
          onConfirm={() => void leaveBoard(boardToLeave)}
          onCancel={() => setBoardToLeave(null)}
        />
      )}

      {boardToDelete && (
        <ConfirmDialog
          title="Delete this board?"
          message={`"${boardToDelete.name}" and all of its buckets and post-its will be deleted. This can't be undone.`}
          confirmLabel="Delete board"
          onConfirm={() => void deleteBoard(boardToDelete)}
          onCancel={() => setBoardToDelete(null)}
        />
      )}
    </div>
  )
}
