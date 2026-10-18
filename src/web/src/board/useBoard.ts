import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, errorMessage } from '../api/client'
import type { BoardDetail } from '../api/types'

/** How often the open board is re-read, so changes made by AI agents and other people show up. */
const POLL_MS = 4000

export interface BoardState {
  /** The loaded board, or null while it loads. */
  board: BoardDetail | null
  /** Runs a mutation, then reloads the board so the UI always shows the server's layout. Resolves to false on failure. */
  run: (action: () => Promise<unknown>) => Promise<boolean>
  /** Holds off background refreshes, e.g. while something is being dragged. */
  setPaused: (paused: boolean) => void
}

/** A 404 for the open board means it was deleted or you were removed from it. */
const isGone = (error: unknown) => error instanceof ApiError && error.status === 404

export function useBoard(
  boardId: string | null,
  onError: (message: string) => void,
  /** Called when the board is gone or you lost access to it. */
  onLost: () => void,
): BoardState {
  const [loaded, setLoaded] = useState<BoardDetail | null>(null)
  const paused = useRef(false)

  useEffect(() => {
    if (!boardId) return
    let ignore = false
    const load = (background: boolean) =>
      api.getBoard(boardId).then(
        (board) => !ignore && (!background || !paused.current) && setLoaded(board),
        (error) => {
          if (ignore) return
          if (isGone(error)) onLost()
          // Otherwise a failed background refresh keeps showing the last good board.
          else if (!background) onError(errorMessage(error))
        },
      )

    void load(false)
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible' && !paused.current) void load(true)
    }, POLL_MS)
    return () => {
      ignore = true
      window.clearInterval(timer)
    }
  }, [boardId, onError, onLost])

  const run = useCallback(
    async (action: () => Promise<unknown>) => {
      let ok = true
      try {
        await action()
      } catch (error) {
        onError(errorMessage(error))
        ok = false
      }
      if (boardId) {
        try {
          setLoaded(await api.getBoard(boardId))
        } catch (error) {
          if (isGone(error)) onLost()
          else onError(errorMessage(error))
        }
      }
      return ok
    },
    [boardId, onError, onLost],
  )

  const setPaused = useCallback((value: boolean) => {
    paused.current = value
  }, [])

  // A board loaded for a previously selected id is not shown for the new one.
  return { board: loaded?.id === boardId ? loaded : null, run, setPaused }
}
