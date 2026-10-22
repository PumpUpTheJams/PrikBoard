import { useCallback, useEffect, useRef, useSyncExternalStore } from 'react'
import { api, errorMessage } from '../api/client'
import { apply, check, type UndoDirection, type UndoEntry } from './undo'

/** How many steps each board keeps. */
const LIMIT = 100
/** Steps with the same coalesce key this close together become one. */
const COALESCE_MS = 10_000

interface Stacks {
  undo: UndoEntry[]
  redo: UndoEntry[]
}

/**
 * The undo history of every board opened in this tab, kept while the tab is open (switching boards keeps it, a reload
 * starts afresh). Entries are plain data, so they outlive the board view that recorded them.
 */
export class UndoHistory {
  private boards = new Map<string, Stacks>()
  private listeners = new Set<() => void>()
  private version = 0

  stacks(boardId: string): Stacks {
    let stacks = this.boards.get(boardId)
    if (!stacks) this.boards.set(boardId, (stacks = { undo: [], redo: [] }))
    return stacks
  }

  /** Adds a step; a new step means the steps undone before can't be redone any more. */
  record(boardId: string, entry: UndoEntry) {
    const stacks = this.stacks(boardId)
    const top = stacks.undo.at(-1)
    if (top && entry.coalesce && top.coalesce === entry.coalesce && entry.at - top.at < COALESCE_MS) {
      // The first step's before, the last one's after.
      for (const change of entry.changes) {
        const same = top.changes.find((c) => c.target.kind === change.target.kind && c.target.id === change.target.id)
        if (same) same.after = change.after
        else top.changes.push(change)
      }
      top.at = entry.at
    } else {
      stacks.undo.push(entry)
      if (stacks.undo.length > LIMIT) stacks.undo.shift()
    }
    stacks.redo = []
    this.emit()
  }

  emit() {
    this.version++
    for (const listener of this.listeners) listener()
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  snapshot = () => this.version
}

interface UndoOptions {
  boardId: string
  history: UndoHistory
  /** Runs a change and reloads the board (not recorded). */
  run: (action: () => Promise<unknown>) => Promise<boolean>
  /** Shows a message; with an action, as a button in it. */
  notify: (message: { text: string; tone?: 'warn'; action?: { label: string; onAction: () => void } }) => void
  /** Whether someone else is dragging an item right now. */
  moving: (id: string) => boolean
  /** Called after a step was taken, e.g. to close a card that is gone now. */
  onApplied?: (entry: UndoEntry, direction: UndoDirection) => void
}

/** Undo and redo on one board: recording steps, and taking them one at a time. */
export function useUndo({ boardId, history, run, notify, moving, onApplied }: UndoOptions) {
  useSyncExternalStore(history.subscribe, history.snapshot)
  const stacks = history.stacks(boardId)
  /** Changes still being saved: an undo waits for them, so it never overtakes the step it should undo. */
  const inflight = useRef(new Set<Promise<unknown>>())
  /** Undo and redo run one after another. */
  const queue = useRef<Promise<void>>(Promise.resolve())
  const latest = useRef({ run, notify, moving, onApplied })
  useEffect(() => {
    latest.current = { run, notify, moving, onApplied }
  })

  const take = useCallback(
    (direction: UndoDirection, only?: number) => {
      queue.current = queue.current.then(async () => {
        await Promise.allSettled([...inflight.current])
        const { run, notify, moving, onApplied } = latest.current
        const stacks = history.stacks(boardId)
        const from = direction === 'undo' ? stacks.undo : stacks.redo
        const entry = from.at(-1)
        // The Undo button in a toast only undoes its own step.
        if (!entry || (only !== undefined && entry.id !== only)) return
        let board
        try {
          board = await api.getBoard(boardId)
        } catch (error) {
          notify({ text: errorMessage(error), tone: 'warn' })
          return
        }
        from.pop()
        history.emit()
        const reason = check(entry, direction, board, moving)
        if (reason) {
          notify({ text: `Can't ${direction} “${entry.label.toLowerCase()}”: ${reason}`, tone: 'warn' })
          return
        }
        if (!(await run(() => apply(entry, direction, board)))) return
        ;(direction === 'undo' ? stacks.redo : stacks.undo).push(entry)
        history.emit()
        onApplied?.(entry, direction)
        notify({ text: `${direction === 'undo' ? 'Undone' : 'Redone'}: ${entry.label.toLowerCase()}` })
      })
      return queue.current
    },
    [boardId, history],
  )

  const record = useCallback(
    (entry: UndoEntry) => {
      history.record(boardId, entry)
      if (entry.toast) latest.current.notify({ text: entry.toast, action: { label: 'Undo', onAction: () => void take('undo', entry.id) } })
    },
    [boardId, history, take],
  )

  /** Keeps track of a change being saved until it is done. */
  const track = useCallback(<T,>(promise: Promise<T>) => {
    inflight.current.add(promise)
    void promise.finally(() => inflight.current.delete(promise))
    return promise
  }, [])

  return {
    record,
    track,
    undo: () => void take('undo'),
    redo: () => void take('redo'),
    /** What the next undo and redo would do, if anything. */
    nextUndo: stacks.undo.at(-1)?.label,
    nextRedo: stacks.redo.at(-1)?.label,
  }
}
