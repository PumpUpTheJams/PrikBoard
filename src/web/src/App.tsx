import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import { api, errorMessage, setUnauthorizedHandler } from './api/client'
import type { BoardSummary, Caller } from './api/types'
import { BoardCanvas } from './board/BoardCanvas'
import {
  CheckIcon,
  ChevronDownIcon,
  DownloadIcon,
  ExternalIcon,
  PeopleIcon,
  PostItIcon,
  RulesIcon,
  ShieldIcon,
  SignOutIcon,
  TrashIcon,
  UploadIcon,
  UserIcon,
} from './board/icons'
import { pickBoardFile, saveFile } from './board/files'
import { boardRefFromHash, isBoard, trashPath } from './board/links'
import { useBoard } from './board/useBoard'
import { UndoHistory } from './board/useUndo'
import { useLive } from './live/useLive'
import { AccountDialog, DisplayNameDialog } from './components/AccountDialogs'
import { AdminPage } from './components/AdminPage'
import { ConfirmDialog } from './components/ConfirmDialog'
import { ConnectPage, connectFromHash } from './components/ConnectPage'
import { Face } from './components/Avatar'
import { InvitePage } from './components/InvitePage'
import { Menu } from './components/Menu'
import { NoAccess } from './components/NoAccess'
import { Presence } from './components/Presence'
import { QuickAdd } from './components/QuickAdd'
import { ShareDialog } from './components/ShareDialog'
import { SignIn } from './components/SignIn'

const BoardRulesDialog = lazy(() => import('./components/BoardRulesDialog').then((m) => ({ default: m.BoardRulesDialog })))

/**
 * Invite links look like /#/invite/{token}. Signed out, the token goes along in the sign-in's return URL, so the
 * server can let a newcomer with an open invite in; otherwise only the request body of preview and accept carries it.
 */
const inviteFromHash = () => window.location.hash.match(/^#\/invite\/([\w-]+)/)?.[1] ?? null
const adminFromHash = () => window.location.hash === '#/admin'

export default function App() {
  // undefined while checking the session, null when signed out.
  const [user, setUser] = useState<Caller | null | undefined>(undefined)
  const [invite, setInvite] = useState(inviteFromHash)
  const [connect, setConnect] = useState(connectFromHash)

  useEffect(() => {
    // Any 401, such as an expired session, drops back to the sign-in screen.
    setUnauthorizedHandler(() => setUser(null))
    api.whoAmI().then(setUser, () => setUser(null))

    const onHashChange = () => {
      setInvite(inviteFromHash())
      setConnect(connectFromHash())
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  async function signOut() {
    await api.logout().catch(() => {})
    setUser(null)
  }

  if (user === undefined) return <p className="status">Loading…</p>
  // Signing in returns to the same hash, so an invite link or an app's sign-in survives the round trip.
  if (user === null) return <SignIn />
  if (connect) {
    return (
      <ConnectPage
        request={connect}
        user={user}
        onDone={() => {
          window.location.hash = ''
        }}
      />
    )
  }
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
  const [selectedId, setSelectedId] = useState(boardRefFromHash)
  const [adminView, setAdminView] = useState(adminFromHash)
  // A board in the hash that isn't in your list is looked up once more (you may just have joined) before the sad page.
  const [recheckedId, setRecheckedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [serverVersion, setServerVersion] = useState<string | null>(null)
  useEffect(() => {
    api.getVersion().then((v) => setServerVersion(v.version), () => {})
  }, [])
  const [boardToDelete, setBoardToDelete] = useState<BoardSummary | null>(null)
  const [boardToLeave, setBoardToLeave] = useState<BoardSummary | null>(null)
  const [dialog, setDialog] = useState<'share' | 'account' | 'rules' | null>(null)

  const loadBoards = useCallback(() => api.listBoards().then(setBoards, (e) => setError(errorMessage(e))), [])

  useEffect(() => {
    void loadBoards()

    const onHashChange = () => {
      setSelectedId(boardRefFromHash())
      setAdminView(adminFromHash())
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [loadBoards])

  const selectedMissing = boards !== null && selectedId !== null && !boards.some((b) => isBoard(b, selectedId))
  useEffect(() => {
    if (!selectedMissing || recheckedId === selectedId) return
    void loadBoards().finally(() => setRecheckedId(selectedId))
  }, [selectedMissing, selectedId, recheckedId, loadBoards])

  // Fall back to the newest board when nothing is selected; a board you can't open gets the sad page instead.
  const activeBoard = adminView || selectedMissing ? null : (boards?.find((b) => isBoard(b, selectedId)) ?? boards?.[0] ?? null)
  const isOwner = activeBoard?.yourRole === 'Owner'

  // The board was deleted or you were removed from it (by an owner or admin, possibly in another tab): the board's
  // link stays and shows the sad page. The canvas keeps the open board in the hash, so read it from there.
  const onLost = useCallback(() => {
    setDialog(null)
    setSelectedId(boardRefFromHash())
    void loadBoards()
  }, [loadBoards])
  // Live updates tell the open board to reload when others save; until connected, the board polls.
  const refreshBoard = useRef(() => {})
  const live = useLive(activeBoard?.id ?? null, { onBoardChanged: () => refreshBoard.current(), onBoardsChanged: loadBoards })
  const { board, run, setPaused, refresh } = useBoard(activeBoard?.id ?? null, setError, onLost, live.connected)
  /** Undo history of every board opened in this tab; switching boards keeps it. */
  const [undoHistory] = useState(() => new UndoHistory())
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
  const provider = user.identities.find((i) => i.provider === user.provider)?.providerName ?? user.provider

  /** Opens a board by its short id (what its address shows), or none. */
  function selectBoard(shortId: string | null) {
    window.location.hash = shortId ? `/boards/${shortId}` : ''
    setSelectedId(shortId)
  }

  async function createBoard(name: string) {
    try {
      const created = await api.createBoard(name)
      setBoards((current) => [created, ...(current ?? [])])
      selectBoard(created.shortId)
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function updateBoard(target: BoardSummary, changes: { name?: string; key?: string }) {
    try {
      const updated = await api.updateBoard(target.id, changes)
      setBoards((current) => current?.map((b) => (b.id === updated.id ? updated : b)) ?? null)
      // Post-its show the key in their readable ids (MPCT-415).
      if (changes.key !== undefined) refresh()
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function exportBoard(target: BoardSummary) {
    try {
      const { blob, fileName } = await api.exportBoard(target.id)
      saveFile(blob, fileName)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  /** Picks an export file and opens the board made from it. */
  async function importBoard() {
    const file = await pickBoardFile()
    if (!file) return
    try {
      const created = await api.importBoard(file)
      setBoards((current) => [created, ...(current ?? [])])
      selectBoard(created.shortId)
    } catch (e) {
      setError(errorMessage(e))
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
          <span
            className="board-tile"
            title={serverVersion ? `PrikBoard server ${serverVersion}` : undefined}
          >
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
                {activeBoard && (
                  <span className="board-key" title={`Post-its on this board are numbered ${activeBoard.key}-1, ${activeBoard.key}-2, …`}>
                    {activeBoard.key}
                  </span>
                )}
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
                      selectBoard(b.shortId)
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
                  <QuickAdd label="Rename board" placeholder={activeBoard.name} onAdd={(name) => updateBoard(activeBoard, { name })} />
                )}
                {activeBoard && isOwner && (
                  // The key goes before post-it numbers (MPCT-415); addresses use the short id, so links keep working.
                  <QuickAdd
                    label="Change key"
                    placeholder={`${activeBoard.key}, as in ${activeBoard.key}-1`}
                    onAdd={(key) => updateBoard(activeBoard, { key })}
                  />
                )}
                {board && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    title="What this board is about and how agents should handle its notes"
                    onClick={() => {
                      close()
                      setDialog('rules')
                    }}
                  >
                    <RulesIcon />
                    Agent rules
                  </button>
                )}
                {board && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    title="Post-its deleted in the last 7 days"
                    onClick={() => {
                      close()
                      // The board opens its trash window from the address.
                      window.location.hash = trashPath(board.shortId).slice(1)
                    }}
                  >
                    <TrashIcon />
                    Deleted items
                  </button>
                )}
                {activeBoard && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    onClick={() => {
                      close()
                      void exportBoard(activeBoard)
                    }}
                  >
                    <DownloadIcon />
                    Export board
                  </button>
                )}
                <button
                  type="button"
                  role="menuitem"
                  className="menu-item"
                  onClick={() => {
                    close()
                    void importBoard()
                  }}
                >
                  <UploadIcon />
                  Import board
                </button>
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
          {activeBoard && (
            <button type="button" className="share-button" onClick={() => setDialog('share')}>
              <PeopleIcon />
              Share
            </button>
          )}
        </div>

        <div className="topbar-end">
          <Presence viewers={othersHere} />
          <span className="topbar-divider" aria-hidden="true" />
          <Menu
            label="Your settings"
            align="end"
            triggerClassName="settings-trigger"
            trigger={
              <>
                <span className="me">
                  <Face name={user.displayName} avatarUrl={user.avatarUrl} />
                </span>
                <ChevronDownIcon />
              </>
            }
          >
            {(close) => (
              <>
                <div className="menu-user">
                  <span className="me">
                  <Face name={user.displayName} avatarUrl={user.avatarUrl} />
                </span>
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
                {user.isAdmin && (
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    onClick={() => {
                      close()
                      window.location.hash = '/admin'
                    }}
                  >
                    <ShieldIcon />
                    Admin
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
        {adminView ? (
          user.isAdmin ? (
            <AdminPage
              onYouChanged={() => void api.whoAmI().then(onUserChange)}
              onBack={() => selectBoard(null)}
            />
          ) : (
            <NoAccess
              title="This page is for admins"
              message="Only admins of this PrikBoard can see it."
              onBack={() => selectBoard(null)}
            />
          )
        ) : boards === null ? (
          <p className="status">Loading…</p>
        ) : selectedMissing ? (
          recheckedId === selectedId ? <NoAccess onBack={() => selectBoard(null)} /> : <p className="status">Loading…</p>
        ) : activeBoard ? (
          board ? (
            <BoardCanvas
              key={activeBoard.id}
              board={board}
              run={run}
              undoHistory={undoHistory}
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
      {dialog === 'rules' && board && (
        <Suspense>
          <BoardRulesDialog
            board={board}
            onSave={(rules) => run(() => api.updateBoard(board.id, { rules }))}
            onClose={() => setDialog(null)}
          />
        </Suspense>
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
