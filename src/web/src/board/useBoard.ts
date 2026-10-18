import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, errorMessage } from '../api/client'
import type { BoardDetail } from '../api/types'

/** How often the open board is re-read without live updates, so changes by agents and other people show up. */
const POLL_MS = 4000
/** With live updates connected, polling is only a safety net. */
const LIVE_POLL_MS = 30_000
/** Change notifications arriving within this window cause one reload. */
const REFRESH_DEBOUNCE_MS = 100

export interface BoardState {
  /** The loaded board, or null while it loads. */
  board: BoardDetail | null
  /** Runs a mutation, then reloads the board so the UI always shows the server's layout. Resolves to false on failure. */
  run: (action: () => Promise<unknown>) => Promise<boolean>
  /** Holds off background refreshes, e.g. while something is being dragged. */
  setPaused: (paused: boolean) => void
  /** Reloads soon in the background, e.g. because someone else changed the board; waits while paused. */
  refresh: () => void
}

/** A 404 for the open board means it was deleted or you were removed from it. */
const isGone = (error: unknown) => error instanceof ApiError && error.status === 404

export function useBoard(
  boardId: string | null,
  onError: (message: string) => void,
  /** Called when the board is gone or you lost access to it. */
  onLost: () => void,
  /** Live updates are connected, so the board only needs an occasional poll. */
  live = false,
): BoardState {
  const [loaded, setLoaded] = useState<BoardDetail | null>(null)
  const paused = useRef(false)
  /** A refresh was asked for while paused; it runs on unpause. */
  const owed = useRef(false)
  const reload = useRef<() => void>(() => {})

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

    let debounce: number | undefined
    reload.current = () => {
      window.clearTimeout(debounce)
      debounce = window.setTimeout(() => {
        if (paused.current) owed.current = true
        else void load(true)
      }, REFRESH_DEBOUNCE_MS)
    }

    void load(false)
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible' && !paused.current) void load(true)
    }, live ? LIVE_POLL_MS : POLL_MS)
    return () => {
      ignore = true
      window.clearInterval(timer)
      window.clearTimeout(debounce)
      reload.current = () => {}
    }
  }, [boardId, onError, onLost, live])

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
    if (!value && owed.current) {
      owed.current = false
      reload.current()
    }
  }, [])

  const refresh = useCallback(() => reload.current(), [])

  // A board loaded for a previously selected id is not shown for the new one.
  return { board: loaded?.id === boardId ? loaded : null, run, setPaused, refresh }
}
