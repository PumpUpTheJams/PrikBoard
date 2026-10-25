import { lazy, Suspense, useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { api, ApiError, errorMessage, setUnauthorizedHandler } from './api/client'
import type { BoardDetail, BoardSummary, Caller, LiveViewer, SetupStatus } from './api/types'
import { BoardCanvas } from './board/BoardCanvas'
import { CardIcon, PeopleIcon } from './board/icons'
import { pickBoardFile, saveFile } from './board/files'
import { boardRefFromHash, isBoard } from './board/links'
import { useBoard, type BoardState } from './board/useBoard'
import { UndoHistory } from './board/useUndo'
import { useLive, type LiveState } from './live/useLive'
import { rememberBoard, rememberedBoard } from './lastBoard'
import { MobileLayout, useLayoutMode } from './layoutMode'
import { MobileWorkspace } from './mobile/MobileWorkspace'
import { AccountDialog, DisplayNameDialog } from './components/AccountDialogs'
import { AdminPage } from './components/AdminPage'
import { BoardMenu } from './components/BoardMenu'
import { ConfirmDialog } from './components/ConfirmDialog'
import { NoticeDialog } from './components/NoticeDialog'
import { ConnectPage } from './components/ConnectPage'
import { connectFromHash } from './components/connect'
import { ChatAccountPage } from './components/ChatAccountPage'
import { chatAccountLinkFromHash } from './components/discord'
import { IntegrationsDialog } from './components/IntegrationsDialog'
import { InvitePage } from './components/InvitePage'
import { NoAccess } from './components/NoAccess'
import { Presence } from './components/Presence'
import { QuickAdd } from './components/QuickAdd'
import { SettingsMenu } from './components/SettingsMenu'
import { SetupPage } from './components/SetupPage'
import { ShareDialog } from './components/ShareDialog'
import { SignIn } from './components/SignIn'
import { takeAuthError } from './components/authError'
import { VisitorPage } from './components/VisitorPage'

const BoardRulesDialog = lazy(() => import('./components/BoardRulesDialog').then((m) => ({ default: m.BoardRulesDialog })))

/**
 * Invite links look like /#/invite/{token}. Signed out, the token goes along in the sign-in's return URL, so the
 * server can let a newcomer with an open invite in; otherwise only the request body of preview and accept carries it.
 */
const inviteFromHash = () => window.location.hash.match(/^#\/invite\/([\w-]+)/)?.[1] ?? null
const adminFromHash = () => window.location.hash === '#/admin'
const setupFromHash = () => window.location.hash === '#/setup'

/** The board in the address isn't in your list: still being looked up once more, or not one you can open. */
type MissingBoard = 'checking' | 'gone' | null

/** The board in the address, or else (`fallBack`) the one open last (`remembered`), or the newest one. */
function pickActiveBoard(boards: BoardSummary[] | null, selectedId: string | null, fallBack: boolean, remembered: string | null) {
  if (!boards) return null
  const selected = boards.find((b) => isBoard(b, selectedId))
  if (selected || !fallBack) return selected ?? null
  return boards.find((b) => b.shortId === remembered) ?? boards[0] ?? null
}

export default function App() {
  // Undefined while checking the session, null when signed out.
  const [user, setUser] = useState<Caller | null | undefined>(undefined)
  const [invite, setInvite] = useState(inviteFromHash)
  const [connect, setConnect] = useState(connectFromHash)
  const [accountLink, setAccountLink] = useState(chatAccountLinkFromHash)
  // Signed out, a board link may be to a public board: shown to visitors unless it isn't public or they sign in.
  const [boardRef, setBoardRef] = useState(boardRefFromHash)
  const [notVisible, setNotVisible] = useState<string | null>(null)
  const [signingIn, setSigningIn] = useState(false)
  // Read before the session check: a failed sign-in shows on the sign-in screen, a failed link over the workspace.
  const [authError, setAuthError] = useState(takeAuthError)
  // Signed out: whether PrikBoard still needs setting up (undefined while asking), which shows the setup instead of signing in.
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null | undefined>(undefined)
  const [setupHash, setSetupHash] = useState(setupFromHash)

  useEffect(() => {
    // Any 401, such as an expired session, drops back to the sign-in screen.
    setUnauthorizedHandler(() => setUser(null))
    api.whoAmI().then(setUser, () => setUser(null))

    const onHashChange = () => {
      setInvite(inviteFromHash())
      setConnect(connectFromHash())
      setAccountLink(chatAccountLinkFromHash())
      setBoardRef(boardRefFromHash())
      setSetupHash(setupFromHash())
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  const loadSetupStatus = useCallback(() => api.getSetupStatus().then(setSetupStatus, () => setSetupStatus(null)), [])
  useEffect(() => {
    if (user === null) void loadSetupStatus()
  }, [user, loadSetupStatus])

  async function signOut() {
    await api.logout().catch(() => {})
    setUser(null)
  }

  if (user === undefined) return <p className="status">Loading…</p>
  // Signing in returns to the same hash, so an invite link, an app's sign-in or a public board survives the round trip.
  if (user === null) {
    if (setupStatus === undefined) return <p className="status">Loading…</p>
    if (setupStatus?.needed || (setupHash && setupStatus)) {
      return (
        <SetupPage
          user={null}
          status={setupStatus}
          onUnlocked={() => void loadSetupStatus()}
          onDone={() => {
            window.location.hash = ''
          }}
        />
      )
    }
    // A refused sign-in (a newcomer without an invite) comes back to the board it started from, and says why over it.
    if (boardRef && !signingIn && !invite && !connect && notVisible !== boardRef) {
      return (
        <>
          <VisitorPage key={boardRef} boardRef={boardRef} onMissing={() => setNotVisible(boardRef)} onSignIn={() => setSigningIn(true)} />
          {authError && <NoticeDialog title="Not signed in" messages={[authError]} onClose={() => setAuthError(null)} />}
        </>
      )
    }
    return <SignIn authError={authError} />
  }
  if (setupHash) {
    return (
      <SetupPage
        user={user}
        status={null}
        onUnlocked={() => {}}
        onDone={() => {
          window.location.hash = ''
        }}
      />
    )
  }
  if (accountLink !== null) {
    return (
      <ChatAccountPage
        platform={accountLink.platform}
        code={accountLink.code}
        user={user}
        onDone={() => {
          window.location.hash = ''
        }}
      />
    )
  }
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
      {authError && <NoticeDialog title="Sign-in not connected" messages={[authError]} onClose={() => setAuthError(null)} />}
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
  // A board in the hash that isn't in your list is looked up once more (you may just have joined), then opened as a
  // visitor if it is public, and else gets the sad page.
  const [recheckedId, setRecheckedId] = useState<string | null>(null)
  const [notPublic, setNotPublic] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  /** Why an import was refused, one message each. */
  const [importRefused, setImportRefused] = useState<string[] | null>(null)
  const [serverVersion, setServerVersion] = useState<string | null>(null)
  useEffect(() => {
    api.getVersion().then((v) => setServerVersion(v.version), () => {})
  }, [])
  const [boardToDelete, setBoardToDelete] = useState<BoardSummary | null>(null)
  const [boardToLeave, setBoardToLeave] = useState<BoardSummary | null>(null)
  const [dialog, setDialog] = useState<'share' | 'account' | 'rules' | 'integrations' | null>(null)
  /** Phones get the mobile layout (lists, a screen at a time); touch devices can switch in the settings menu. */
  const layout = useLayoutMode()
  const mobile = layout.mode === 'mobile' && !adminView

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

  // Fall back to the board open last on this device, or the newest one, when nothing is selected (the mobile layout
  // lists the boards instead). A board you can't open gets the sad page.
  const activeBoard = adminView || selectedMissing ? null : pickActiveBoard(boards, selectedId, !mobile, rememberedBoard(user.id))
  const missing: MissingBoard = selectedMissing ? (recheckedId === selectedId ? 'gone' : 'checking') : null
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
  const { board, run, setPaused, refresh, loads } = useBoard(activeBoard?.id ?? null, setError, onLost, live.connected)
  /** Undo history of every board opened in this tab; switching boards keeps it. */
  const [undoHistory] = useState(() => new UndoHistory())
  const { boardLoaded } = live
  useEffect(() => {
    refreshBoard.current = refresh
  }, [refresh])
  // Your name shows on cards (creator, assignee). Others hear about a rename live; this tab reloads itself.
  const shownName = useRef(user.displayName)
  useEffect(() => {
    if (shownName.current === user.displayName) return
    shownName.current = user.displayName
    refresh()
  }, [user.displayName, refresh])
  // Someone's finished drag stays where they dropped it until the reload shows where it landed.
  useEffect(() => boardLoaded(), [loads, boardLoaded])
  const othersHere = live.viewers.filter((v) => v.principalId !== user.id)
  /** Who points at the board, or has something selected or dragged on it, if it shows cursors: their avatar in the top bar goes there. */
  const pointers = useSyncExternalStore(live.cursors.subscribe, live.cursors.principals)
  const pickable = activeBoard?.showCursors
    ? new Set([...pointers.split(' ').filter(Boolean), ...live.drags.filter((d) => !d.endedAt).map((d) => d.principalId)])
    : undefined
  const [focus, setFocus] = useState<{ principalId: string; n: number } | null>(null)
  /** Whether someone else is dragging an item right now (undo waits for that). */
  const movedByOthers = (id: string) => live.drags.some((r) => !r.endedAt && (r.id === id || r.items?.includes(id)))

  /** Opens a board by its short id (what its address shows), or none. */
  function selectBoard(shortId: string | null) {
    window.location.hash = shortId ? `/boards/${shortId}` : ''
    setSelectedId(shortId)
  }

  /** The board open before the admin page, to go back to from there. */
  const lastBoard = useRef<string | null>(null)
  useEffect(() => {
    if (!activeBoard) return
    lastBoard.current = activeBoard.shortId
    rememberBoard(user.id, activeBoard.shortId)
  }, [activeBoard, user.id])

  /**
   * Leaves the admin page for the board that was open before it. The list is loaded again first, since boards and roles
   * may have changed there; when that board is gone, the newest one opens.
   */
  function leaveAdmin() {
    const last = lastBoard.current
    api.listBoards().then(
      (list) => {
        setBoards(list)
        selectBoard(last && list.some((b) => b.shortId === last) ? last : null)
      },
      () => selectBoard(last),
    )
  }

  /** Runs a change; a failure shows in the error bar. Resolves whether it worked. */
  async function guard(action: () => Promise<unknown>) {
    try {
      await action()
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  /** A board made here (created or imported): first in the list, and opened. */
  function addBoard(created: BoardSummary) {
    setBoards((current) => [created, ...(current ?? [])])
    selectBoard(created.shortId)
  }

  const replaceBoard = (updated: BoardSummary) => setBoards((current) => current?.map((b) => (b.id === updated.id ? updated : b)) ?? null)

  /** A board gone from your list (deleted, or you left it): nothing is open then. */
  function dropBoard(target: BoardSummary) {
    setBoards((current) => current?.filter((b) => b.id !== target.id) ?? null)
    selectBoard(null)
  }

  const createBoard = (name: string) => guard(async () => addBoard(await api.createBoard(name)))

  const updateBoard = (target: BoardSummary, changes: { name?: string; key?: string }) =>
    guard(async () => {
      replaceBoard(await api.updateBoard(target.id, changes))
      // Cards show the key in their readable ids (MPCT-415).
      if (changes.key !== undefined) refresh()
    })

  const exportBoard = (target: BoardSummary) =>
    guard(async () => {
      const { blob, fileName } = await api.exportBoard(target.id)
      saveFile(blob, fileName)
    })

  /** Picks an export file and opens the board made from it. */
  async function importBoard() {
    const file = await pickBoardFile()
    if (!file) return
    try {
      addBoard(await api.importBoard(file))
    } catch (e) {
      // A refused file says why (a newer PrikBoard made it, it isn't an export, ...): a dialog, so it isn't missed.
      if (e instanceof ApiError && e.status === 400) setImportRefused([...new Set(Object.values(e.errors).flat())].slice(0, 5))
      else setError(errorMessage(e))
    }
  }

  async function leaveBoard(target: BoardSummary) {
    setBoardToLeave(null)
    await guard(async () => {
      await api.removeBoardMember(target.id, user.id)
      left(target)
    })
  }

  /** You are no longer on this board. */
  function left(target: BoardSummary) {
    setDialog(null)
    dropBoard(target)
  }

  async function deleteBoard(target: BoardSummary) {
    setBoardToDelete(null)
    await guard(async () => {
      await api.deleteBoard(target.id)
      dropBoard(target)
    })
  }

  // A board you're not a member of may be public: you see it as its visitors do, until it turns out not to be.
  if (!adminView && selectedId && selectedMissing && recheckedId === selectedId && notPublic !== selectedId) {
    return <VisitorPage key={selectedId} boardRef={selectedId} onMissing={() => setNotPublic(selectedId)} onBack={() => selectBoard(null)} />
  }

  const dialogs = (
    <>
      {dialog === 'share' && activeBoard && (
        <ShareDialog
          key={activeBoard.id}
          board={activeBoard}
          onClose={() => setDialog(null)}
          onLeft={() => left(activeBoard)}
          onChanged={replaceBoard}
        />
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
      {dialog === 'integrations' && activeBoard && isOwner && (
        <IntegrationsDialog
          key={activeBoard.id}
          board={activeBoard}
          buckets={board?.buckets ?? []}
          isAdmin={user.isAdmin}
          onClose={() => setDialog(null)}
        />
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
          message={`"${boardToDelete.name}" and all of its buckets and cards will be deleted. This can't be undone.`}
          confirmLabel="Delete board"
          onConfirm={() => void deleteBoard(boardToDelete)}
          onCancel={() => setBoardToDelete(null)}
        />
      )}

      {importRefused && <NoticeDialog title="Can't import this board" messages={importRefused} onClose={() => setImportRefused(null)} />}
    </>
  )

  const errorBar = error && (
    <div className="error" role="alert">
      <span>{error}</span>
      <button type="button" className="ghost" onClick={() => setError(null)}>
        Dismiss
      </button>
    </div>
  )

  if (mobile) {
    return (
      <MobileLayout.Provider value>
        <div className="app mobile">
          {errorBar}
          <MobileWorkspace
            user={user}
            boards={boards}
            activeBoard={activeBoard}
            board={board}
            missing={missing}
            run={run}
            undoHistory={undoHistory}
            moving={movedByOthers}
            onCreateBoard={createBoard}
            onDialog={setDialog}
            onSignOut={onSignOut}
            onDesktop={() => layout.choose('desktop')}
          />
          {dialogs}
        </div>
      </MobileLayout.Provider>
    )
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-start">
          <span
            className="board-tile"
            title={serverVersion ? `PrikBoard server ${serverVersion}` : undefined}
          >
            <CardIcon size={16} />
          </span>
          <h1 className="visually-hidden">{activeBoard?.name ?? 'PrikBoard'}</h1>
          <BoardMenu
            boards={boards}
            activeBoard={activeBoard}
            board={board}
            onSelect={selectBoard}
            onCreate={createBoard}
            onUpdate={updateBoard}
            onDialog={setDialog}
            onExport={(target) => void exportBoard(target)}
            onImport={() => void importBoard()}
            onLeave={setBoardToLeave}
            onDelete={setBoardToDelete}
          />
          {activeBoard && (
            <button type="button" className="share-button" onClick={() => setDialog('share')}>
              <PeopleIcon />
              Share
            </button>
          )}
        </div>

        <div className="topbar-end">
          <Presence
            viewers={othersHere}
            guests={live.guests}
            pickable={pickable}
            onPick={(principalId) => setFocus((current) => ({ principalId, n: (current?.n ?? 0) + 1 }))}
          />
          <span className="topbar-divider" aria-hidden="true" />
          <SettingsMenu
            user={user}
            canSwitch={layout.canSwitch}
            onAccount={() => setDialog('account')}
            onMobile={() => layout.choose('mobile')}
            onSignOut={onSignOut}
          />
        </div>
      </header>

      {errorBar}

      <main>
        <WorkspaceMain
          user={user}
          adminView={adminView}
          boards={boards}
          missing={missing}
          activeBoard={activeBoard}
          board={board}
          run={run}
          undoHistory={undoHistory}
          setPaused={setPaused}
          live={live}
          othersHere={othersHere}
          focus={focus}
          onCreateBoard={createBoard}
          onBack={() => selectBoard(null)}
          onLeaveAdmin={leaveAdmin}
          onYouChanged={() => void api.whoAmI().then(onUserChange)}
        />
      </main>

      {dialogs}
    </div>
  )
}

interface WorkspaceMainProps {
  user: Caller
  adminView: boolean
  boards: BoardSummary[] | null
  missing: MissingBoard
  activeBoard: BoardSummary | null
  board: BoardDetail | null
  run: BoardState['run']
  undoHistory: UndoHistory
  setPaused: BoardState['setPaused']
  live: LiveState
  /** The others on the open board. */
  othersHere: LiveViewer[]
  /** Someone picked in the top bar, whose pointer (or pins) the board goes to. */
  focus: { principalId: string; n: number } | null
  onCreateBoard: (name: string) => Promise<boolean>
  /** Leaves a board you can't open, for none. */
  onBack: () => void
  onLeaveAdmin: () => void
  onYouChanged: () => void
}

/** What the desktop shows under the top bar: the admin page, the open board, or why there is none. */
function WorkspaceMain(props: WorkspaceMainProps) {
  const { user, adminView, boards, missing, activeBoard, board, run, undoHistory, setPaused, live, othersHere, focus } = props
  const { onCreateBoard, onBack, onLeaveAdmin, onYouChanged } = props

  if (adminView) {
    if (user.isAdmin) return <AdminPage onYouChanged={onYouChanged} onBack={onLeaveAdmin} />
    return <NoAccess title="This page is for admins" message="Only admins of this PrikBoard can see it." onBack={onLeaveAdmin} />
  }
  if (boards === null || missing === 'checking') return <p className="status">Loading…</p>
  if (missing === 'gone') return <NoAccess onBack={onBack} />
  if (!activeBoard) {
    return (
      <div className="empty">
        <span className="board-tile large" aria-hidden="true">
          <CardIcon size={28} />
        </span>
        <h2>No boards yet</h2>
        <p>Create a board for your project, then start sticking cards on it.</p>
        <QuickAdd label="Create a board" placeholder="Project name" onAdd={onCreateBoard} />
      </div>
    )
  }
  if (!board) return <p className="status">Loading board…</p>
  return (
    <BoardCanvas
      key={activeBoard.id}
      board={board}
      run={run}
      undoHistory={undoHistory}
      setPaused={setPaused}
      remoteDrags={live.drags}
      onDrag={live.sendDrag}
      cursors={live.cursors}
      viewers={othersHere}
      onCursor={activeBoard.showCursors ? live.sendCursor : undefined}
      onSelect={activeBoard.showCursors ? live.sendSelection : undefined}
      focus={focus}
    />
  )
}
