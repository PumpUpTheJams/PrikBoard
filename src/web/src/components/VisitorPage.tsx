import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import { BoardCanvas } from '../board/BoardCanvas'
import { CardIcon, RulesIcon } from '../board/icons'
import { useBoard } from '../board/useBoard'
import { UndoHistory } from '../board/useUndo'
import { useLive } from '../live/useLive'
import { Presence } from './Presence'

const BoardRulesDialog = lazy(() => import('./BoardRulesDialog').then((m) => ({ default: m.BoardRulesDialog })))

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/

interface VisitorPageProps {
  /** The board in the address: its short id, or its id. */
  boardRef: string
  /** The board isn't public (or is gone, or became members-only): show the sign-in page, or the sad page, instead. */
  onMissing: () => void
  /** Signed out: a way to sign in. */
  onSignIn?: () => void
  /** Signed in (not a member): a way back to your own boards. */
  onBack?: () => void
}

/**
 * A public board for someone who isn't a member, signed in or not: read only, with its name, its agent rules and how
 * many people look at it at the top, and live updates. Search, the filters and card links work as for members.
 */
export function VisitorPage({ boardRef, onMissing, onSignIn, onBack }: VisitorPageProps) {
  const [boardId, setBoardId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [rulesOpen, setRulesOpen] = useState(false)
  const [undoHistory] = useState(() => new UndoHistory())
  const missing = useRef(onMissing)
  useEffect(() => {
    missing.current = onMissing
  })

  useEffect(() => {
    let ignore = false
    const lookup = GUID.test(boardRef) ? api.getBoard(boardRef) : api.getBoardByShortId(boardRef)
    lookup.then(
      (found) => !ignore && setBoardId(found.id),
      () => !ignore && missing.current(),
    )
    return () => {
      ignore = true
    }
  }, [boardRef])

  // Made members-only meanwhile (or deleted).
  const onLost = useCallback(() => missing.current(), [])
  const refreshBoard = useRef(() => {})
  const live = useLive(boardId, { onBoardChanged: () => refreshBoard.current(), onBoardsChanged: () => {} })
  const { board, run, setPaused, refresh } = useBoard(boardId, setError, onLost, live.connected)
  useEffect(() => {
    refreshBoard.current = refresh
  }, [refresh])
  const { boardLoaded } = live
  useEffect(() => boardLoaded(), [board, boardLoaded])

  // Guests count you too; when the board hides its people, they are everyone looking.
  const others = Math.max(0, live.guests - 1)
  const hidden = !!board && !board.publicShowsPeople

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-start">
          <span className="board-tile">
            <CardIcon size={16} />
          </span>
          <h1 className="visitor-board-name" title={board?.description ?? undefined}>
            {board?.name ?? 'PrikBoard'}
          </h1>
          {board && <span className="board-key">{board.key}</span>}
          {board?.rules && (
            <button type="button" className="ghost visitor-rules" onClick={() => setRulesOpen(true)}>
              <RulesIcon />
              Agent rules
            </button>
          )}
        </div>
        <div className="topbar-end">
          <Presence viewers={live.viewers} guests={others} anonymous={hidden} />
          <span className="topbar-divider" aria-hidden="true" />
          {onSignIn && (
            <button type="button" className="primary small" onClick={onSignIn}>
              Sign in
            </button>
          )}
          {onBack && (
            <button type="button" className="ghost" onClick={onBack}>
              Your boards
            </button>
          )}
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
        {board ? (
          <BoardCanvas key={board.id} board={board} run={run} undoHistory={undoHistory} setPaused={setPaused} remoteDrags={[]} />
        ) : (
          <p className="status">Loading board…</p>
        )}
      </main>

      {rulesOpen && board && (
        <Suspense>
          <BoardRulesDialog board={board} onSave={() => Promise.resolve(false)} onClose={() => setRulesOpen(false)} />
        </Suspense>
      )}
    </div>
  )
}
