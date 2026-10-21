import { useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { api, errorMessage } from '../api/client'
import { ConfirmDialog } from '../components/ConfirmDialog'
import type { ImageDetail, NoteDetail, Point, TrashContents } from '../api/types'
import { isDrag } from './gesture'
import { CloseIcon, MinusIcon, PlusIcon, SearchIcon, TrashIcon } from './icons'
import { fitGrid, GRID, NOTE_HEIGHT, NOTE_WIDTH } from './layout'
import { pop } from './motion'
import { PostIt } from './PostIt'
import { matchNote, searchTerms } from './search'
import type { TagColors } from './tagColors'
import { useFloatingWindow } from './useFloatingWindow'
import { useZoom } from './useZoom'
import { stepZoom, ZOOM_LEVELS } from './zoom'

/** What the board needs from the open trash: whether a post-it dragged on the board is over it, and a pop when one went in. */
export interface TrashWindowHandle {
  contains: (client: Point) => boolean
  pop: () => void
}

/**
 * A post-it dragged out of the window: the pointer (screen pixels), where on the post-it it was grabbed (note pixels),
 * and whether the pointer is outside the window.
 */
export interface OutgoingNote {
  note: NoteDetail
  client: Point
  grab: Point
  outside: boolean
}

/** A press on a post-it in the window, in screen pixels; a drag once it travels far enough. */
interface Press {
  note: NoteDetail
  start: Point
  current: Point
  grab: Point
  outside: boolean
}

const NO_INSET = { x: 0, y: 0 }

interface TrashWindowProps {
  boardId: string
  /** Changes with every reload of the board: the window then loads the trash again. */
  reload: unknown
  readOnly: boolean
  tagColors: TagColors
  handle: RefObject<TrashWindowHandle | null>
  /** A post-it from the board is being dragged over the window, to be deleted. */
  dropOver: boolean
  boardZoom: number
  onClose: () => void
  /** Shows a post-it as it is inside, read only. */
  onOpenNote: (note: NoteDetail, images: ImageDetail[]) => void
  onPutBack: (note: NoteDetail) => void
  /** Deletes a post-it in the trash for good; resolves false when that failed. */
  onPurge: (note: NoteDetail) => Promise<boolean>
  /** Deletes everything in the trash for good (after the window asked); resolves false when that failed. */
  onEmpty: () => Promise<boolean>
  /** A post-it is being dragged out of the window (null once that ends). */
  onOutgoing: (drag: OutgoingNote | null) => void
  /** A post-it dragged out of the window was released outside it; false when it went nowhere (it stays in the trash). */
  onDropOut: (drag: OutgoingNote) => boolean
  /** Whether a drag is in progress, so the board holds back its refreshes. */
  onBusy: (busy: boolean) => void
}

/**
 * The trash opened up: a window floating over the board, like an expanded post-it, with its own small canvas. The post-its
 * deleted in the last 7 days fill it in a grid, most recently deleted first, as wide as the window shows; the search at the
 * top filters them and the rest close up. A post-it goes back with its put-back button, or by dragging it out onto the
 * board.
 */
export function TrashWindow(props: TrashWindowProps) {
  const { boardId, reload, readOnly, tagColors, handle, dropOver, boardZoom, onClose, onOpenNote, onPutBack } = props
  const { onPurge, onEmpty, onOutgoing, onDropOut, onBusy } = props
  const dialogRef = useRef<HTMLElement>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  const { rect, moving, startMove, startResize } = useFloatingWindow(dialogRef, 'prikboard.trashWindow.window')
  const { zoom, zoomTo } = useZoom(scrollRef, NO_INSET)
  const [contents, setContents] = useState<TrashContents | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string | null>(null)
  const [press, setPress] = useState<Press | null>(null)
  /** Asking whether to empty the trash. */
  const [confirmEmpty, setConfirmEmpty] = useState(false)
  /** Post-its dropped out of the window: hidden until the next load no longer has them. */
  const [gone, setGone] = useState<Set<string>>(new Set())
  /** The scroll viewport in screen pixels, which the grid fits. */
  const [view, setView] = useState({ width: 0, height: 0 })
  /** The post-its the last two presses were on: the canvas holds the pointer, so double-clicks reach it, not the post-it. */
  const presses = useRef<[string | null, string | null]>([null, null])

  useEffect(() => {
    let current = true
    api.listDeletedNotes(boardId).then(
      (loaded) => {
        if (!current) return
        setContents(loaded)
        setGone(new Set())
        setError(null)
      },
      (failure) => current && setError(errorMessage(failure)),
    )
    return () => {
      current = false
    }
  }, [boardId, reload])

  useEffect(() => onBusy(press !== null), [press, onBusy])
  useEffect(() => () => onBusy(false), [onBusy])

  useEffect(() => searchRef.current?.focus({ preventScroll: true }), [])

  useLayoutEffect(() => {
    const el = scrollRef.current!
    const update = () => setView({ width: el.clientWidth, height: el.clientHeight })
    update()
    const observer = new ResizeObserver(update)
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  const contains = (client: Point) => {
    const r = dialogRef.current?.getBoundingClientRect()
    return !!r && client.x >= r.left && client.x < r.right && client.y >= r.top && client.y < r.bottom
  }

  useImperativeHandle(handle, () => ({ contains, pop: () => pop(dialogRef.current, 1.015) }))

  const terms = useMemo(() => searchTerms(query), [query])
  const all = (contents?.notes ?? []).filter((n) => !gone.has(n.id))
  const shown = all.filter((n) => terms.length === 0 || matchNote(n, terms) !== null)
  /** The grid fits what is visible of the canvas, so zooming out fits more columns. */
  const width = Math.max(NOTE_WIDTH + 2 * GRID, Math.floor(view.width / zoom))
  const spots = fitGrid(shown.length, width)
  const height = Math.max(Math.floor(view.height / zoom), GRID + Math.max(0, ...spots.map((s) => s.y + NOTE_HEIGHT)))
  const imagesOf = (noteId: string) => contents?.images.filter((i) => i.noteId === noteId) ?? []
  const dragging = press && isDrag(press) ? press : null

  function toCanvas(client: Point): Point {
    const r = canvasRef.current!.getBoundingClientRect()
    return { x: (client.x - r.left) / zoom, y: (client.y - r.top) / zoom }
  }

  function onNotePointerDown(e: PointerEvent<HTMLDivElement>, note: NoteDetail, at: Point) {
    presses.current = [presses.current[1], note.id]
    setSelected(note.id)
    if (e.button !== 0) return
    e.stopPropagation()
    e.preventDefault()
    e.currentTarget.focus({ preventScroll: true })
    if (readOnly) return
    canvasRef.current!.setPointerCapture(e.pointerId)
    const client = { x: e.clientX, y: e.clientY }
    const p = toCanvas(client)
    setPress({ note, start: client, current: client, grab: { x: p.x - at.x, y: p.y - at.y }, outside: false })
  }

  function onPointerMove(e: PointerEvent) {
    if (!press) return
    const client = { x: e.clientX, y: e.clientY }
    const next = { ...press, current: client, outside: !contains(client) }
    setPress(next)
    if (isDrag(next)) onOutgoing({ note: next.note, client, grab: next.grab, outside: next.outside })
  }

  function onPointerUp() {
    if (!press) return
    setPress(null)
    onOutgoing(null)
    if (!dragging || !dragging.outside) return
    // Hidden until the reload takes it out of the trash, so it doesn't jump back into the grid meanwhile.
    if (onDropOut({ note: dragging.note, client: dragging.current, grab: dragging.grab, outside: true })) {
      setGone((g) => new Set(g).add(dragging.note.id))
    }
  }

  /** Hidden until the next load no longer has it. */
  async function purge(note: NoteDetail) {
    if (await onPurge(note)) setGone((g) => new Set(g).add(note.id))
  }

  async function empty() {
    setConfirmEmpty(false)
    if (await onEmpty()) setGone(new Set(all.map((n) => n.id)))
  }

  function cancel() {
    if (!press) return
    setPress(null)
    onOutgoing(null)
  }

  const ghostZoom = dragging?.outside ? boardZoom : zoom

  return (
    <article
      ref={dialogRef}
      className={`expanded trash-window${moving ? ' moving' : ''}${dropOver ? ' drop-over' : ''}`}
      style={rect ? { left: rect.x, top: rect.y, width: rect.width, height: rect.height } : { visibility: 'hidden' }}
      role="dialog"
      aria-modal="false"
      aria-label="Deleted items"
      tabIndex={-1}
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return
        e.stopPropagation()
        if (e.target === searchRef.current && query) setQuery('')
        else onClose()
      }}
    >
      <header className="band trash-band" onPointerDown={startMove}>
        <div className="band-row">
          <span className="trash-band-title">
            <TrashIcon size={18} />
            Deleted items
            <span className="count">{all.length}</span>
          </span>
          <div className="band-actions">
            {!readOnly && (
              <button
                type="button"
                className="band-button empty-trash"
                title="Delete everything in here for good"
                disabled={all.length === 0}
                onClick={() => setConfirmEmpty(true)}
              >
                <TrashIcon size={15} />
                Empty trash
              </button>
            )}
            <button type="button" className="band-button icon" aria-label="Close" title="Close (Esc)" onClick={onClose}>
              <CloseIcon />
            </button>
          </div>
        </div>
        <label className="trash-search">
          <SearchIcon />
          <input
            ref={searchRef}
            type="search"
            placeholder="Search deleted items"
            aria-label="Search deleted items"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {terms.length > 0 && <span className="trash-search-count">{shown.length === 1 ? '1 match' : `${shown.length} matches`}</span>}
        </label>
      </header>

      <div className="note-viewport">
        <div ref={scrollRef} className="note-scroll trash-scroll" tabIndex={-1} aria-label="Deleted post-its">
          <div className="canvas-sizer" style={{ width: width * zoom, height: height * zoom }}>
            <div
              ref={canvasRef}
              className="note-canvas trash-canvas"
              style={{ width, height, transform: `scale(${zoom})` }}
              onPointerDown={(e) => {
                if (e.target !== e.currentTarget) return
                presses.current = [presses.current[1], null]
                setSelected(null)
              }}
              onPointerMove={onPointerMove}
              onPointerUp={onPointerUp}
              onPointerCancel={cancel}
              onLostPointerCapture={cancel}
              onDoubleClick={() => {
                const [first, second] = presses.current
                const note = second && first === second && shown.find((n) => n.id === second)
                if (note) onOpenNote(note, imagesOf(note.id))
              }}
            >
              {shown.map((note, i) => (
                <div key={note.id} className={dragging?.note.id === note.id ? 'leaving' : undefined}>
                  <PostIt
                    note={note}
                    point={spots[i]}
                    images={imagesOf(note.id).length}
                    tagColors={tagColors}
                    selected={selected === note.id}
                    lifted={false}
                    highlight={terms.length > 0 ? terms : undefined}
                    onPointerDown={(e) => onNotePointerDown(e, note, spots[i])}
                    onExpand={() => onOpenNote(note, imagesOf(note.id))}
                    onPutBack={readOnly ? undefined : () => onPutBack(note)}
                    onPurge={readOnly ? undefined : () => void purge(note)}
                  />
                </div>
              ))}
            </div>
          </div>
        </div>

        {error ? (
          <p className="note-empty">{error}</p>
        ) : contents && shown.length === 0 ? (
          <p className="note-empty">
            {terms.length > 0 ? 'No deleted items match the search.' : 'Nothing was deleted in the last 7 days. Deleted post-its stay here that long.'}
          </p>
        ) : null}

        <div className="zoom-controls note-zoom">
          <button type="button" className="rail-button small" aria-label="Zoom out" disabled={zoom <= ZOOM_LEVELS[0]} onClick={() => zoomTo(stepZoom(zoom, -1))}>
            <MinusIcon />
          </button>
          <button type="button" className="zoom-level" title="Reset to 100%" onClick={() => zoomTo(1)}>
            {Math.round(zoom * 100)}%
          </button>
          <button
            type="button"
            className="rail-button small"
            aria-label="Zoom in"
            disabled={zoom >= ZOOM_LEVELS[ZOOM_LEVELS.length - 1]}
            onClick={() => zoomTo(stepZoom(zoom, 1))}
          >
            <PlusIcon />
          </button>
        </div>
      </div>

      <span className="window-resize e" aria-hidden="true" onPointerDown={startResize('e')} />
      <span className="window-resize s" aria-hidden="true" onPointerDown={startResize('s')} />
      <span className="window-resize se" aria-hidden="true" onPointerDown={startResize('se')} />

      {/* A post-it on its way out follows the pointer: at the window's zoom inside it, at the board's outside. */}
      {dragging &&
        createPortal(
          <div
            className="drag-ghost post-it-ghost"
            aria-hidden="true"
            style={{
              left: dragging.current.x - dragging.grab.x * ghostZoom,
              top: dragging.current.y - dragging.grab.y * ghostZoom,
              transform: `scale(${ghostZoom})`,
            }}
          >
            <PostIt note={dragging.note} point={{ x: 0, y: 0 }} images={imagesOf(dragging.note.id).length} tagColors={tagColors} selected={false} lifted onPointerDown={() => {}} onExpand={() => {}} />
          </div>,
          document.body,
        )}

      {confirmEmpty &&
        createPortal(
          <ConfirmDialog
            title="Empty the trash?"
            message={`${all.length === 1 ? 'The post-it in here, with its' : `All ${all.length} post-its in here, with their`} images and drawings, will be deleted for good. This can't be undone.`}
            confirmLabel="Empty trash"
            onConfirm={() => void empty()}
            onCancel={() => setConfirmEmpty(false)}
          />,
          document.body,
        )}
    </article>
  )
}
