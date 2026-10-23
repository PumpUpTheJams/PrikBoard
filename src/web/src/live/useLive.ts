import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useCallback, useEffect, useRef, useState } from 'react'
import { setLiveConnectionId } from '../api/client'
import type { BoardChange, BoardPresence, CursorUpdate, DragUpdate, JoinedBoard, LiveViewer, RemoteCursor, RemoteDrag, RemoteSelection } from '../api/types'

/** How often a drag in progress or the pointer is sent; the server drops updates closer together than 25 ms. */
const SEND_MS = 50
/** A drag nobody updated for this long is dropped (its tab probably froze or lost the network). */
const STALE_DRAG_MS = 5000
/** How often a drag held still is sent again, so the others don't drop it as stale. */
const DRAG_HEARTBEAT_MS = 2000

/** Someone else's drag; `endedAt` is set once they let go, and the item stays put until the board reloads. */
export type LiveDrag = RemoteDrag & { updatedAt: number; endedAt?: number }

/** Someone else's pointer on the open board, and when it last moved. */
export type LiveCursor = RemoteCursor & { updatedAt: number }

/** Others' pointers and selections on the open board, as `CursorStore.snapshot` gives them. */
export interface Peers {
  cursors: LiveCursor[]
  /** Only those with something selected. */
  selections: RemoteSelection[]
}

/**
 * Others' pointers and selections on the open board. Pointers move many times a second, so they are kept outside React
 * state: only what subscribes (the heads and pins) draws again. `principals`: who has a pointer or a selection, as a
 * string that only changes when that does.
 */
export interface CursorStore {
  snapshot: () => Peers
  principals: () => string
  subscribe: (listener: () => void) => () => void
}

export interface LiveHandlers {
  /** Someone else saved a change to the open board. */
  onBoardChanged: () => void
  /** Your list of boards changed (renamed, deleted, added or removed). */
  onBoardsChanged: () => void
}

export interface LiveState {
  connected: boolean
  /** Everyone on the open board, including you (members only; see guests). */
  viewers: LiveViewer[]
  /** How many visitors of a public board are looking at it (for visitors of one that hides people: everyone, you too). */
  guests: number
  /** Drags by others (and your other tabs) on the open board. */
  drags: LiveDrag[]
  /** Sends your drag in progress (throttled), or null when it ends. */
  sendDrag: (update: DragUpdate | null) => void
  /** Others' pointers and selections on the open board (members only, and only while the board shows cursors). */
  cursors: CursorStore
  /** Sends where your pointer is on the board (throttled), or null when it left the board. */
  sendCursor: (point: CursorUpdate | null) => void
  /** Sends what you have selected on the board (throttled); none when nothing is. */
  sendSelection: (items: string[]) => void
  /** Call after the board reloaded: drags that ended are now shown where they landed. */
  boardLoaded: () => void
}

/**
 * Live updates for the open board over SignalR (/api/live): reloads when others save, who is here, and others' drags
 * and pointers.
 * The connection reconnects on its own; while it is down the board falls back to polling.
 */
export function useLive(boardId: string | null, handlers: LiveHandlers): LiveState {
  const connection = useRef<HubConnection | null>(null)
  const [connected, setConnected] = useState(false)
  const [presence, setPresence] = useState<BoardPresence | null>(null)
  // Tagged with their board, so switching boards drops them without a reset.
  const [drags, setDrags] = useState<{ boardId: string | null; list: LiveDrag[] }>({ boardId: null, list: [] })
  const [cursors] = useState(cursorStore)
  const handlersRef = useRef(handlers)
  const boardRef = useRef(boardId)

  useEffect(() => {
    handlersRef.current = handlers
  })

  // One connection for as long as the board view is open (signed in, or visiting a public board).
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
    hub.on('CursorMoved', (cursor: RemoteCursor) => cursors.move(cursor))
    hub.on('CursorHidden', (key: string) => cursors.hide(key))
    hub.on('Selected', (selection: RemoteSelection) => cursors.select(selection))

    const opened = () => {
      setLiveConnectionId(hub.connectionId)
      setConnected(true)
      if (boardRef.current) void join(hub, boardRef.current)
    }
    hub.onreconnecting(() => {
      setLiveConnectionId(null)
      setConnected(false)
      // Nobody tells a dropped connection that pointers and selections went away.
      cursors.clear()
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
  }, [cursors])

  async function join(hub: HubConnection, id: string) {
    if (hub.state !== HubConnectionState.Connected) return
    try {
      // Null when the board is gone or not yours (any more): the board itself will say so.
      const joined = await hub.invoke<JoinedBoard | null>('Join', id)
      if (joined && boardRef.current === id) setPresence({ boardId: id, viewers: joined.viewers, guests: joined.guests })
      // After reconnecting, the others see your selection again.
      if (joined && selection.current.length > 0) void hub.send('Select', { items: selection.current }).catch(() => {})
    } catch {
      // The connection dropped; it joins again once it reconnects.
    }
  }

  // Follow the open board. (Connecting and reconnecting join the board that is open then.)
  useEffect(() => {
    boardRef.current = boardId
    cursors.clear()
    const hub = connection.current
    if (!hub || hub.state !== HubConnectionState.Connected) return
    if (boardId) void join(hub, boardId)
    else void hub.invoke('Leave').catch(() => {})
  }, [boardId, cursors])

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

  // Throttled sending: the latest drag update or pointer goes out at most every SEND_MS.
  const send = useCallback((method: string, ...args: unknown[]) => {
    const hub = connection.current
    if (hub?.state === HubConnectionState.Connected) void hub.send(method, ...args).catch(() => {})
  }, [])
  const drag = useThrottled<DragUpdate>((update) => send('Drag', update))
  const cursor = useThrottled<CursorUpdate>((point) => send('MoveCursor', point))
  const selecting = useThrottled<string[]>((items) => send('Select', { items }))
  /** What you have selected, as last sent. */
  const selection = useRef<string[]>([])
  /** The drag in progress, sent again now and then while it is held still; null when there is none. */
  const dragging = useRef<{ update: DragUpdate; heartbeat: number } | null>(null)
  const pointing = useRef(false)

  const sendDrag = useCallback(
    (update: DragUpdate | null) => {
      if (update) {
        if (dragging.current) dragging.current.update = update
        else {
          const heartbeat = window.setInterval(() => dragging.current && drag.push(dragging.current.update), DRAG_HEARTBEAT_MS)
          dragging.current = { update, heartbeat }
        }
        drag.push(update)
      } else if (dragging.current) {
        window.clearInterval(dragging.current.heartbeat)
        dragging.current = null
        drag.flush()
        send('DragEnd')
      }
    },
    [drag, send],
  )
  // A drag still held when the board view closes stops beating.
  useEffect(() => () => window.clearInterval(dragging.current?.heartbeat), [])

  const sendCursor = useCallback(
    (point: CursorUpdate | null) => {
      if (point) {
        pointing.current = true
        cursor.push(point)
      } else if (pointing.current) {
        pointing.current = false
        cursor.cancel()
        send('HideCursor')
      }
    },
    [cursor, send],
  )

  const sendSelection = useCallback(
    (items: string[]) => {
      if (items.length === 0 && selection.current.length === 0) return
      selection.current = items
      selecting.push(items)
    },
    [selecting],
  )

  const boardLoaded = useCallback(() => {
    setDrags((current) => (current.list.some((d) => d.endedAt) ? { ...current, list: current.list.filter((d) => !d.endedAt) } : current))
  }, [])

  return {
    connected,
    viewers: presence?.boardId === boardId ? presence.viewers : [],
    guests: presence?.boardId === boardId ? presence.guests : 0,
    drags: drags.boardId === boardId ? drags.list : [],
    sendDrag,
    cursors,
    sendCursor,
    sendSelection,
    boardLoaded,
  }
}

function cursorStore() {
  let peers: Peers = { cursors: [], selections: [] }
  let principals = ''
  const listeners = new Set<() => void>()
  const set = (next: Peers) => {
    peers = next
    principals = [...new Set([...next.cursors, ...next.selections].map((p) => p.principalId))].toSorted().join(' ')
    for (const listener of listeners) listener()
  }
  return {
    snapshot: () => peers,
    principals: () => principals,
    subscribe(listener: () => void) {
      listeners.add(listener)
      return () => void listeners.delete(listener)
    },
    move(cursor: RemoteCursor) {
      set({ ...peers, cursors: [...peers.cursors.filter((c) => c.key !== cursor.key), { ...cursor, updatedAt: Date.now() }] })
    },
    hide(key: string) {
      if (peers.cursors.some((c) => c.key === key)) set({ ...peers, cursors: peers.cursors.filter((c) => c.key !== key) })
    },
    select(selection: RemoteSelection) {
      const others = peers.selections.filter((s) => s.key !== selection.key)
      if (selection.items.length > 0 || others.length < peers.selections.length) {
        set({ ...peers, selections: selection.items.length > 0 ? [...others, selection] : others })
      }
    },
    clear() {
      if (peers.cursors.length > 0 || peers.selections.length > 0) set({ cursors: [], selections: [] })
    },
  }
}

/**
 * Sends the latest value pushed at most every SEND_MS: the first one at once, then whatever came last. `flush` sends
 * what is still waiting and stops; `cancel` drops it.
 */
function useThrottled<T>(send: (value: T) => void) {
  const sendRef = useRef(send)
  useEffect(() => {
    sendRef.current = send
  })
  const [throttle] = useState(() => {
    let pending: T | null = null
    let timer: number | null = null
    const flush = () => {
      if (pending !== null) sendRef.current(pending)
      pending = null
    }
    const stop = () => {
      if (timer !== null) window.clearTimeout(timer)
      timer = null
    }
    const tick = () => {
      if (pending === null) {
        timer = null
        return
      }
      flush()
      timer = window.setTimeout(tick, SEND_MS)
    }
    return {
      push(value: T) {
        pending = value
        if (timer !== null) return
        flush()
        timer = window.setTimeout(tick, SEND_MS)
      },
      flush() {
        stop()
        flush()
      },
      cancel() {
        stop()
        pending = null
      },
    }
  })
  return throttle
}
