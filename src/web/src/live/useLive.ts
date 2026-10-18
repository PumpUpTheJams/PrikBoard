import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useCallback, useEffect, useRef, useState } from 'react'
import { setLiveConnectionId } from '../api/client'
import type { BoardChange, BoardPresence, DragUpdate, JoinedBoard, LiveViewer, RemoteDrag } from '../api/types'

/** How often a drag in progress is sent; the server drops updates closer together than 25 ms. */
const DRAG_SEND_MS = 50
/** A drag nobody updated for this long is dropped (its tab probably froze or lost the network). */
const STALE_DRAG_MS = 5000

/** Someone else's drag; `endedAt` is set once they let go, and the item stays put until the board reloads. */
export type LiveDrag = RemoteDrag & { updatedAt: number; endedAt?: number }

export interface LiveHandlers {
  /** Someone else saved a change to the open board. */
  onBoardChanged: () => void
  /** Your list of boards changed (renamed, deleted, added or removed). */
  onBoardsChanged: () => void
}

export interface LiveState {
  connected: boolean
  /** Everyone on the open board, including you. */
  viewers: LiveViewer[]
  /** Drags by others (and your other tabs) on the open board. */
  drags: LiveDrag[]
  /** Sends your drag in progress (throttled), or null when it ends. */
  sendDrag: (update: DragUpdate | null) => void
  /** Call after the board reloaded: drags that ended are now shown where they landed. */
  boardLoaded: () => void
}

/**
 * Live updates for the open board over SignalR (/api/live): reloads when others save, who is here, and others' drags.
 * The connection reconnects on its own; while it is down the board falls back to polling.
 */
export function useLive(boardId: string | null, handlers: LiveHandlers): LiveState {
  const connection = useRef<HubConnection | null>(null)
  const [connected, setConnected] = useState(false)
  const [presence, setPresence] = useState<BoardPresence | null>(null)
  // Tagged with their board, so switching boards drops them without a reset.
  const [drags, setDrags] = useState<{ boardId: string | null; list: LiveDrag[] }>({ boardId: null, list: [] })
  const handlersRef = useRef(handlers)
  const boardRef = useRef(boardId)

  useEffect(() => {
    handlersRef.current = handlers
  })

  // One connection for as long as someone is signed in.
  useEffect(() => {
    const hub = new HubConnectionBuilder()
      .withUrl('/api/live')
      // Keep trying (1 s, 2 s, 4 s ... up to 30 s): a laptop waking up should come back on its own.
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (retry) => Math.min(30_000, 1000 * 2 ** retry.previousRetryCount) })
      // Polling covers outages, so a dropped connection is not an error worth logging.
      .configureLogging(LogLevel.None)
      .build()

    const isOpen = (id: string) => id === boardRef.current
    hub.on('BoardChanged', (change: BoardChange) => isOpen(change.boardId) && handlersRef.current.onBoardChanged())
    hub.on('BoardsChanged', () => handlersRef.current.onBoardsChanged())
    hub.on('AccessChanged', (id: string) => {
      handlersRef.current.onBoardsChanged()
      // Re-join for the new role; if access is gone, the reload's 404 takes the person off the board.
      if (isOpen(id)) void join(hub, id).finally(() => handlersRef.current.onBoardChanged())
    })
    hub.on('Presence', (update: BoardPresence) => isOpen(update.boardId) && setPresence(update))
    hub.on('Dragging', (drag: RemoteDrag) =>
      setDrags((current) => {
        const list = current.boardId === boardRef.current ? current.list.filter((d) => d.key !== drag.key) : []
        return { boardId: boardRef.current, list: [...list, { ...drag, updatedAt: Date.now() }] }
      }),
    )
    hub.on('DragEnded', (key: string) =>
      setDrags((current) => ({
        ...current,
        list: current.list.map((d) => (d.key === key && !d.endedAt ? { ...d, endedAt: Date.now() } : d)),
      })),
    )

    const opened = () => {
      setLiveConnectionId(hub.connectionId)
      setConnected(true)
      if (boardRef.current) void join(hub, boardRef.current)
    }
    hub.onreconnecting(() => {
      setLiveConnectionId(null)
      setConnected(false)
    })
    hub.onreconnected(() => {
      opened()
      // Changes made while offline were missed.
      handlersRef.current.onBoardChanged()
      handlersRef.current.onBoardsChanged()
    })

    let stopped = false
    let retry: number | undefined
    const start = (attempt: number) =>
      hub.start().then(opened, () => {
        // The first connection is not covered by automatic reconnect.
        if (!stopped) retry = window.setTimeout(() => start(attempt + 1), Math.min(30_000, 1000 * 2 ** attempt))
      })
    void start(0)
    connection.current = hub

    return () => {
      stopped = true
      window.clearTimeout(retry)
      setLiveConnectionId(null)
      connection.current = null
      void hub.stop()
    }
  }, [])

  async function join(hub: HubConnection, id: string) {
    if (hub.state !== HubConnectionState.Connected) return
    try {
      const joined = await hub.invoke<JoinedBoard>('Join', id)
      if (boardRef.current === id) setPresence({ boardId: id, viewers: joined.viewers })
    } catch {
      // Not a member (any more): the board itself will say so.
    }
  }

  // Follow the open board. (Connecting and reconnecting join the board that is open then.)
  useEffect(() => {
    boardRef.current = boardId
    const hub = connection.current
    if (!hub || hub.state !== HubConnectionState.Connected) return
    if (boardId) void join(hub, boardId)
    else void hub.invoke('Leave').catch(() => {})
  }, [boardId])

  // Forget drags whose tab went quiet.
  const dragCount = drags.list.length
  useEffect(() => {
    if (dragCount === 0) return
    const timer = window.setInterval(() => {
      const now = Date.now()
      setDrags((current) => {
        const fresh = current.list.filter((d) => now - (d.endedAt ?? d.updatedAt) < STALE_DRAG_MS)
        return fresh.length === current.list.length ? current : { ...current, list: fresh }
      })
    }, 1000)
    return () => window.clearInterval(timer)
  }, [dragCount])

  // Throttled drag sending: the latest update goes out at most every DRAG_SEND_MS.
  const pending = useRef<DragUpdate | null>(null)
  const timer = useRef<number | null>(null)
  const dragging = useRef(false)

  const sendDrag = useCallback(
    (update: DragUpdate | null) => {
      const send = (method: string, ...args: unknown[]) => {
        const hub = connection.current
        if (hub?.state === HubConnectionState.Connected) void hub.send(method, ...args).catch(() => {})
      }
      const flush = () => {
        if (pending.current) send('Drag', pending.current)
        pending.current = null
      }

      if (update) {
        dragging.current = true
        pending.current = update
        if (timer.current !== null) return
        flush()
        const tick = () => {
          if (!pending.current) {
            timer.current = null
            return
          }
          flush()
          timer.current = window.setTimeout(tick, DRAG_SEND_MS)
        }
        timer.current = window.setTimeout(tick, DRAG_SEND_MS)
      } else if (dragging.current) {
        dragging.current = false
        if (timer.current !== null) window.clearTimeout(timer.current)
        timer.current = null
        flush()
        send('DragEnd')
      }
    },
    [],
  )

  const boardLoaded = useCallback(() => {
    setDrags((current) => (current.list.some((d) => d.endedAt) ? { ...current, list: current.list.filter((d) => !d.endedAt) } : current))
  }, [])

  return {
    connected,
    viewers: presence?.boardId === boardId ? presence.viewers : [],
    drags: drags.boardId === boardId ? drags.list : [],
    sendDrag,
    boardLoaded,
  }
}
