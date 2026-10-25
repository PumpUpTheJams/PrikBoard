import { useCallback, useEffect, useState, type ReactNode } from 'react'
import type { BoardDetail, BoardSummary, Caller, CardDetail } from '../api/types'
import { MoreIcon, PeopleIcon, RulesIcon, SignOutIcon, UserIcon, ExpandIcon } from '../board/icons'
import { bucketInHash, cardInHash, cardLabel, imageInHash, missingCardInHash } from '../board/links'
import { describeMissingCard } from '../board/missingCard'
import { tagColorsOf } from '../board/tagColors'
import { recordingRun } from '../board/ops'
import type { BoardState } from '../board/useBoard'
import { useUndo, type UndoHistory } from '../board/useUndo'
import { Face } from '../components/Avatar'
import { Menu, MenuItem } from '../components/Menu'
import { ThemePicker } from '../components/SettingsMenu'
import { NoAccess } from '../components/NoAccess'
import { Toast, type ToastMessage } from '../components/Toast'
import { BoardList } from './BoardList'
import { BucketList } from './BucketList'
import { BucketScreen } from './BucketScreen'
import { CardScreen } from './CardScreen'
import { ImageScreen } from './ImageScreen'
import { navigate } from './nav'

/** The windows App opens over either layout. */
export type WorkspaceDialog = 'share' | 'account' | 'rules'

interface MobileWorkspaceProps {
  user: Caller
  boards: BoardSummary[] | null
  /** The board in the address, once it is in your list. */
  activeBoard: BoardSummary | null
  board: BoardDetail | null
  /** The board in the address isn't one you can open (or is still being looked up: `checking`). */
  missing: 'checking' | 'gone' | null
  run: BoardState['run']
  undoHistory: UndoHistory
  /** Whether someone else is dragging an item on the board right now (undo waits for that). */
  moving: (id: string) => boolean
  onCreateBoard: (name: string) => Promise<boolean>
  onDialog: (dialog: WorkspaceDialog) => void
  onSignOut: () => void
  /** Switches this device to the desktop layout. */
  onDesktop: () => void
}

/**
 * The app on a phone: boards, then a board's buckets, then a bucket's cards, then one card, a screen at a time. Every
 * screen is an address (board/links.ts), so the same link opens the same thing here and on the desktop.
 */
export function MobileWorkspace(props: MobileWorkspaceProps) {
  const { user, boards, activeBoard, board, missing, onCreateBoard, onDialog, onSignOut, onDesktop } = props

  const settings = (
    <Menu label="Your settings" align="end" triggerClassName="m-bar-button" trigger={<span className="me"><Face name={user.displayName} avatarUrl={user.avatarUrl} /></span>}>
      {() => (
        <>
          <div className="menu-user">
            <span className="menu-user-text">
              <span className="menu-user-name">{user.displayName}</span>
            </span>
          </div>
          <span className="menu-divider" role="separator" />
          {user.kind === 'Session' && (
            <MenuItem icon={<UserIcon />} onClick={() => onDialog('account')}>
              Account
            </MenuItem>
          )}
          <ThemePicker />
          <MenuItem icon={<ExpandIcon />} onClick={onDesktop}>
            Desktop view
          </MenuItem>
          <span className="menu-divider" role="separator" />
          <MenuItem icon={<SignOutIcon />} onClick={onSignOut}>
            Sign out
          </MenuItem>
        </>
      )}
    </Menu>
  )

  if (boards === null || missing === 'checking') return <p className="status">Loading…</p>
  if (missing === 'gone') return <NoAccess onBack={() => navigate('')} />
  if (!activeBoard) return <BoardList boards={boards} onCreate={onCreateBoard} settings={settings} />
  if (!board) return <p className="status">Loading board…</p>
  return <MobileBoard key={board.id} {...props} board={board} />
}

/** One board on the phone: picks the screen from the address, and keeps undo and the toast for all of them. */
function MobileBoard({ board, run, undoHistory, moving, onDialog, onDesktop }: MobileWorkspaceProps & { board: BoardDetail }) {
  const [hash, setHash] = useState(() => window.location.hash)
  useEffect(() => {
    const onHashChange = () => setHash(window.location.hash)
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])
  /** A card just made here: its title is focused when it opens. */
  const [freshId, setFreshId] = useState<string | null>(null)

  const [toast, setToast] = useState<ToastMessage | null>(null)
  const clearToast = useCallback(() => setToast(null), [])
  const notify = (message: string | Omit<ToastMessage, 'id'>) => setToast({ id: Date.now(), ...(typeof message === 'string' ? { text: message } : message) })
  const undo = useUndo({ boardId: board.id, history: undoHistory, run, notify, moving })
  /** Runs a change and records it for undo, the same way the board canvas does. */
  const runRec = recordingRun(run, undo)
  const tagColors = tagColorsOf(board.tags)

  // The screen follows the address (hash is read so this runs again when it changes).
  void hash
  const image = board.images.find((i) => i.id === imageInHash())
  const card = board.cards.find((c) => c.id === cardInHash(board))
  const bucketId = bucketInHash()

  // A link to a card this board doesn't have lands on the board, saying why (there is no trash screen to show it in).
  useEffect(() => {
    void hash
    const route = missingCardInHash(board)
    if (!route) return
    navigate(`/#/boards/${board.shortId}`, { replace: true })
    void describeMissingCard(board, route).then(({ text }) => setToast({ id: Date.now(), text, tone: 'warn' }))
  }, [hash, board])

  // The browser tab names what is on screen, as on the desktop.
  const tabTitle = card ? `${card.readableId} ${cardLabel(card)}` : board.name
  useEffect(() => {
    document.title = tabTitle
  }, [tabTitle])
  useEffect(() => () => void (document.title = 'PrikBoard'), [])

  const actions = (
    <Menu label="Board menu" align="end" triggerClassName="m-bar-button" trigger={<MoreIcon />}>
      {() => (
        <>
          <MenuItem icon={<RulesIcon />} onClick={() => onDialog('rules')}>
            Agent rules
          </MenuItem>
          <MenuItem icon={<PeopleIcon />} onClick={() => onDialog('share')}>
            Share
          </MenuItem>
          <MenuItem icon={<ExpandIcon />} onClick={onDesktop}>
            Desktop view
          </MenuItem>
        </>
      )}
    </Menu>
  )

  function opened(created: CardDetail) {
    setFreshId(created.id)
    navigate(created.path)
  }

  let screen: ReactNode
  if (image) screen = <ImageScreen board={board} image={image} run={runRec} />
  else if (card)
    screen = <CardScreen key={card.id} board={board} card={card} fresh={freshId === card.id} onFreshDone={() => setFreshId(null)} tagColors={tagColors} run={runRec} notify={notify} />
  else if (bucketId) screen = <BucketScreen board={board} bucketId={bucketId} tagColors={tagColors} run={runRec} onCreated={opened} actions={actions} />
  else screen = <BucketList board={board} actions={actions} />

  return (
    <>
      {screen}
      <Toast message={toast} onDone={clearToast} />
    </>
  )
}
