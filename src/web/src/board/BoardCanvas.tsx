import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type PointerEvent } from 'react'
import { api } from '../api/client'
import type { BoardDetail, BucketDetail, DragKind, DragUpdate, ImageDetail, NoteColor, NoteDetail, Point, Rect } from '../api/types'
import { presenceColor } from '../live/presence'
import type { LiveDrag } from '../live/useLive'
import { ConfirmDeleteNote, ConfirmDialog } from '../components/ConfirmDialog'
import { BoardImageView } from './BoardImageView'
import { ExpandedNote } from './ExpandedNote'
import { draggingFiles, imageFiles, pickImageFiles } from './files'
import { ResizeIcon } from './icons'
import { BucketInspector, ImageInspector } from './Inspector'
import {
  MIN_BUCKET_HEIGHT,
  MIN_BUCKET_WIDTH,
  NOTE_HEIGHT,
  NOTE_WIDTH,
  bucketUnderNote,
  imageSize,
  snap,
  snapPoint,
  spanRect,
} from './layout'
import { PostIt } from './PostIt'
import { ToolRail } from './ToolRail'
import { toolKeys, type Tool } from './tools'
import type { BoardState } from './useBoard'
import { ViewControls } from './ViewControls'
import { stepZoom } from './zoom'

interface Selection {
  kind: 'note' | 'bucket' | 'image'
  id: string
  /** Just created: its name or title is focused so it can be typed straight away. */
  fresh?: boolean
}

/**
 * A pointer gesture on the canvas, in canvas coordinates. A finished gesture (`done`) stays until the board has
 * reloaded, so the dropped item doesn't jump back to its old place while the request is in flight.
 */
type Gesture = GestureTarget & { start: Point; current: Point; done?: boolean }
type ItemGesture = 'note' | 'bucket' | 'resize' | 'image' | 'image-resize'
type GestureTarget = { kind: ItemGesture; id: string } | { kind: 'draw' }

/** A pan in progress: where the pointer went down and the scroll position at that moment, in screen pixels. */
interface Pan {
  x: number
  y: number
  left: number
  top: number
}

/** Pointer travel (px) before a press on a post-it or bucket counts as a drag rather than a click. */
const DRAG_THRESHOLD = 4
/** Empty canvas kept beyond the furthest item, so there is always room to drag things further out. */
const CANVAS_MARGIN = 600
/** Screen space left of the canvas, so the tool rail doesn't cover the left edge of the board. */
const RAIL_INSET = 72
/** Where a new image's top-left goes relative to the point it was dropped, pasted or added at. */
const IMAGE_OFFSET = { x: 180, y: 120 }

const hints: Partial<Record<Tool, string>> = {
  pan: 'Drag to look around the board',
  note: 'Click anywhere to stick a post-it',
  bucket: 'Drag across the board to draw a bucket',
}

const delta = (g: Gesture): Point => ({ x: g.current.x - g.start.x, y: g.current.y - g.start.y })

/** A drag applied on top of the server's layout: your own gesture, or someone else's relayed by live updates. */
interface ActiveDrag {
  kind: ItemGesture
  id: string
  d: Point
}

const fromDragKind: Record<DragKind, ItemGesture> = {
  Note: 'note',
  Bucket: 'bucket',
  Resize: 'resize',
  Image: 'image',
  ImageResize: 'image-resize',
}
const toDragKind = Object.fromEntries(Object.entries(fromDragKind).map(([k, v]) => [v, k])) as Record<ItemGesture, DragKind>

function isDrag(g: Gesture) {
  const d = delta(g)
  return Math.abs(d.x) >= DRAG_THRESHOLD || Math.abs(d.y) >= DRAG_THRESHOLD
}

interface BoardCanvasProps {
  board: BoardDetail
  run: BoardState['run']
  setPaused: BoardState['setPaused']
  /** Other people's drags in progress (live updates). */
  remoteDrags?: LiveDrag[]
  /** Your drag in progress, for the others to see; null when it ends. */
  onDrag?: (update: DragUpdate | null) => void
}

export function BoardCanvas({ board, run, setPaused, remoteDrags = [], onDrag }: BoardCanvasProps) {
  /** Viewers can look around and open post-its, but not change anything. */
  const readOnly = board.yourRole === 'Viewer'
  const [tool, setTool] = useState<Tool>('select')
  const [fill, setFill] = useState<NoteColor>('Butter')
  const [selection, setSelection] = useState<Selection | null>(null)
  const [expanded, setExpanded] = useState<{ id: string; fresh: boolean } | null>(null)
  /** Something waiting for the person to confirm its deletion (Delete key on the board, or the image panel). */
  const [pendingDelete, setPendingDelete] = useState<{ kind: 'note' | 'image'; id: string } | null>(null)
  const [gesture, setGesture] = useState<Gesture | null>(null)
  const [pan, setPan] = useState<Pan | null>(null)
  const [zoom, setZoom] = useState(1)
  /** Image uploads in flight, and whether files are being dragged over the board. */
  const [uploads, setUploads] = useState(0)
  const [fileOver, setFileOver] = useState(false)
  /** The scroll viewport, in screen pixels. */
  const [view, setView] = useState<Rect>({ x: 0, y: 0, width: 0, height: 0 })
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  /** A canvas point to keep under the same screen point once a new zoom level has rendered. */
  const zoomAnchor = useRef<{ canvas: Point; screen: Point } | null>(null)
  /** Last pointer position over the canvas, in canvas pixels: where pasted images go. */
  const lastPointer = useRef<Point | null>(null)

  // Background refreshes would fight with a drag in progress.
  useEffect(() => setPaused(gesture !== null), [gesture, setPaused])

  useLayoutEffect(() => {
    const el = scrollRef.current!
    const update = () => setView({ x: el.scrollLeft, y: el.scrollTop, width: el.clientWidth, height: el.clientHeight })
    update()
    const observer = new ResizeObserver(update)
    observer.observe(el)
    el.addEventListener('scroll', update, { passive: true })
    return () => {
      observer.disconnect()
      el.removeEventListener('scroll', update)
    }
  }, [])

  useLayoutEffect(() => {
    const anchor = zoomAnchor.current
    if (!anchor) return
    zoomAnchor.current = null
    const el = scrollRef.current!
    el.scrollLeft = anchor.canvas.x * zoom + RAIL_INSET - anchor.screen.x
    el.scrollTop = anchor.canvas.y * zoom - anchor.screen.y
  }, [zoom])

  /** Zooms keeping the canvas point under `screen` (relative to the viewport; default its centre) in place. */
  function zoomTo(next: number, screen?: Point) {
    if (next === zoom) return
    const el = scrollRef.current!
    const at = screen ?? { x: el.clientWidth / 2, y: el.clientHeight / 2 }
    zoomAnchor.current = { canvas: { x: (el.scrollLeft + at.x - RAIL_INSET) / zoom, y: (el.scrollTop + at.y) / zoom }, screen: at }
    setZoom(next)
  }

  // Ctrl/Cmd + wheel zooms around the pointer. React's wheel listener is passive, so register it by hand.
  useEffect(() => {
    const el = scrollRef.current!
    function onWheel(e: WheelEvent) {
      if (!e.ctrlKey && !e.metaKey) return
      e.preventDefault()
      const rect = el.getBoundingClientRect()
      zoomTo(stepZoom(zoom, e.deltaY < 0 ? 1 : -1), { x: e.clientX - rect.left, y: e.clientY - rect.top })
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  })

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (expanded || pendingDelete) return
      if (e.target instanceof Element && e.target.closest('input, textarea, select, [role="menu"], [role="alertdialog"]')) return
      if (e.key === 'Escape') {
        setTool('select')
        setSelection(null)
      } else if (readOnly) {
        if (e.key.toLowerCase() === 'v' || e.key.toLowerCase() === 'h') setTool(toolKeys[e.key.toLowerCase()])
      } else if ((e.key === 'Delete' || e.key === 'Backspace') && (selection?.kind === 'note' || selection?.kind === 'image')) {
        e.preventDefault()
        setPendingDelete({ kind: selection.kind, id: selection.id })
      } else if (e.ctrlKey || e.metaKey || e.altKey) {
        return
      } else if (e.key.toLowerCase() === 'i') {
        void addImagesFromPicker()
      } else if (toolKeys[e.key.toLowerCase()]) {
        setTool(toolKeys[e.key.toLowerCase()])
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  // Pasting an image puts it on the board where the pointer was, or attaches it to the open post-it.
  useEffect(() => {
    function onPaste(e: ClipboardEvent) {
      const files = imageFiles(e.clipboardData)
      if (readOnly || files.length === 0) return
      e.preventDefault()
      void addImages(files, expanded ? { noteId: expanded.id } : { at: lastPointer.current ?? undefined })
    }
    window.addEventListener('paste', onPaste)
    return () => window.removeEventListener('paste', onPaste)
  })

  /** Runs a change through the board, then lets go of the finished gesture. */
  async function settle(action: () => Promise<unknown>) {
    const ok = await run(action)
    setGesture(null)
    return ok
  }

  /** Deletes a post-it the person has confirmed, closing it if it was open. */
  function deleteNote(id: string) {
    setPendingDelete(null)
    if (expanded?.id === id) setExpanded(null)
    setSelection(null)
    void settle(() => api.deleteNote(id))
  }

  function deleteImage(id: string) {
    setPendingDelete(null)
    if (selection?.id === id) setSelection(null)
    void settle(() => api.deleteImage(id))
  }

  /** The middle of what is on screen, in canvas pixels. */
  function viewportCenter(): Point {
    return { x: (view.x - RAIL_INSET + view.width / 2) / zoom, y: (view.y + view.height / 2) / zoom }
  }

  /**
   * Uploads images one after another: onto the board around `at` (default: the middle of the screen), cascading so
   * they don't cover each other, or attached to a note. A single image on the board is selected afterwards.
   */
  async function addImages(files: File[], target: { at?: Point; noteId?: string }) {
    if (files.length === 0) return
    const at = target.at ?? viewportCenter()
    setUploads((n) => n + 1)
    await run(async () => {
      let last: ImageDetail | undefined
      for (const [i, file] of files.entries()) {
        const placement = target.noteId
          ? { noteId: target.noteId }
          : snapPoint({ x: at.x - IMAGE_OFFSET.x + i * 40, y: at.y - IMAGE_OFFSET.y + i * 40 })
        last = await api.uploadImage(board.id, file, placement)
      }
      if (last && !target.noteId && files.length === 1) setSelection({ kind: 'image', id: last.id })
    })
    setUploads((n) => n - 1)
  }

  async function addImagesFromPicker(target: { noteId?: string } = {}) {
    await addImages(await pickImageFiles(), target)
  }

  // Live geometry: the server's layout, with the drags in progress applied on top (yours wins over someone else's).
  const dragging = gesture && gesture.kind !== 'draw' && isDrag(gesture) ? gesture : null
  const drags: ActiveDrag[] = [
    ...(dragging ? [{ kind: dragging.kind, id: dragging.id, d: delta(dragging) }] : []),
    ...remoteDrags.map((r) => ({ kind: fromDragKind[r.kind], id: r.id, d: { x: r.dx, y: r.dy } })),
  ]
  const dragOf = (id: string | null) => (id === null ? undefined : drags.find((g) => g.id === id))

  // Tell the others about your drag while it is in progress (as a string, so an unchanged drag sends nothing).
  const sentDrag =
    dragging && !dragging.done
      ? JSON.stringify({ kind: toDragKind[dragging.kind], id: dragging.id, dx: delta(dragging).x, dy: delta(dragging).y })
      : null
  useEffect(() => onDrag?.(sentDrag ? (JSON.parse(sentDrag) as DragUpdate) : null), [sentDrag, onDrag])

  function bucketRect(bucket: BucketDetail): Rect {
    const drag = dragOf(bucket.id)
    if (!drag) return bucket
    const { d } = drag
    if (drag.kind === 'bucket') return { ...bucket, ...snapPoint({ x: bucket.x + d.x, y: bucket.y + d.y }) }
    if (drag.kind === 'resize') {
      return {
        ...bucket,
        width: Math.max(MIN_BUCKET_WIDTH, snap(bucket.width + d.x)),
        height: Math.max(MIN_BUCKET_HEIGHT, snap(bucket.height + d.y)),
      }
    }
    return bucket
  }

  function notePoint(note: NoteDetail): Point {
    const drag = dragOf(note.id)
    if (drag?.kind === 'note') return snapPoint({ x: note.x + drag.d.x, y: note.y + drag.d.y })
    if (dragOf(note.bucketId)?.kind === 'bucket') {
      // A moving bucket carries its notes along, as the server does.
      const bucket = board.buckets.find((b) => b.id === note.bucketId)!
      const moved = bucketRect(bucket)
      return { x: Math.max(0, note.x + moved.x - bucket.x), y: Math.max(0, note.y + moved.y - bucket.y) }
    }
    return note
  }

  function imageRect(image: ImageDetail): Rect {
    const drag = dragOf(image.id)
    if (!drag) return image
    const { d } = drag
    if (drag.kind === 'image') return { ...image, ...snapPoint({ x: image.x + d.x, y: image.y + d.y }) }
    if (drag.kind === 'image-resize') return { ...image, ...imageSize(image, image.width + d.x) }
    return image
  }

  /** Where someone else's drag is right now, to outline it with their name. */
  function remoteRect(drag: LiveDrag): Rect | null {
    const kind = fromDragKind[drag.kind]
    if (kind === 'note') {
      const note = board.notes.find((n) => n.id === drag.id)
      return note ? { ...notePoint(note), width: NOTE_WIDTH, height: NOTE_HEIGHT } : null
    }
    if (kind === 'bucket' || kind === 'resize') {
      const bucket = board.buckets.find((b) => b.id === drag.id)
      return bucket ? bucketRect(bucket) : null
    }
    const image = board.images.find((i) => i.id === drag.id && i.noteId === null)
    return image ? imageRect(image) : null
  }

  function toCanvas(e: { clientX: number; clientY: number }): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: (e.clientX - rect.left) / zoom, y: (e.clientY - rect.top) / zoom }
  }

  function beginGesture(e: PointerEvent, target: GestureTarget) {
    const point = toCanvas(e)
    canvasRef.current!.setPointerCapture(e.pointerId)
    setGesture({ ...target, start: point, current: point })
  }

  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: ItemGesture, id: string) {
    if (e.button !== 0 || tool !== 'select') return
    e.stopPropagation()
    e.preventDefault()
    if (readOnly) {
      // Post-its can still be selected (and opened with Enter); nothing moves and no panels open.
      if (kind === 'note') {
        e.currentTarget.focus({ preventScroll: true })
        setSelection({ kind: 'note', id })
      }
      return
    }
    if (kind === 'note' || kind === 'image') e.currentTarget.focus({ preventScroll: true })
    setSelection({ kind: kind === 'note' ? 'note' : kind === 'image' || kind === 'image-resize' ? 'image' : 'bucket', id })
    beginGesture(e, { kind, id })
  }

  function onCanvasPointerDown(e: PointerEvent) {
    if (e.button === 1 || (e.button === 0 && tool === 'pan')) {
      e.preventDefault()
      const el = scrollRef.current!
      canvasRef.current!.setPointerCapture(e.pointerId)
      setPan({ x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop })
      return
    }
    if (e.button !== 0) return
    if (tool === 'bucket') {
      e.preventDefault()
      beginGesture(e, { kind: 'draw' })
    } else if (tool === 'note') {
      const p = toCanvas(e)
      const at = snapPoint({ x: p.x - NOTE_WIDTH / 2, y: p.y - NOTE_HEIGHT / 2 })
      void settle(async () => {
        const note = await api.createNote(board.id, { title: 'New post-it', color: fill, ...at })
        setSelection({ kind: 'note', id: note.id })
        setExpanded({ id: note.id, fresh: true })
        setTool('select')
      })
    } else {
      setSelection(null)
    }
  }

  function onCanvasPointerMove(e: PointerEvent) {
    lastPointer.current = toCanvas(e)
    if (pan) {
      const el = scrollRef.current!
      el.scrollLeft = pan.left - (e.clientX - pan.x)
      el.scrollTop = pan.top - (e.clientY - pan.y)
    } else if (gesture && !gesture.done) {
      setGesture({ ...gesture, current: toCanvas(e) })
    }
  }

  function onCanvasPointerUp() {
    if (pan) {
      setPan(null)
      return
    }
    if (!gesture || gesture.done) return
    if (gesture.kind === 'draw') {
      const rect = spanRect(snapPoint(gesture.start), snapPoint(gesture.current))
      if (rect.width < MIN_BUCKET_WIDTH || rect.height < MIN_BUCKET_HEIGHT) {
        setGesture(null)
        return
      }
      setGesture({ ...gesture, done: true })
      void settle(async () => {
        const bucket = await api.createBucket(board.id, { name: 'New bucket', ...rect })
        setSelection({ kind: 'bucket', id: bucket.id, fresh: true })
        setTool('select')
      })
      return
    }
    if (!isDrag(gesture)) {
      setGesture(null)
      return
    }

    setGesture({ ...gesture, done: true })
    const { id } = gesture
    if (gesture.kind === 'note') {
      const note = board.notes.find((n) => n.id === id)!
      void settle(() => api.moveNote(id, notePoint(note)))
    } else if (gesture.kind === 'image' || gesture.kind === 'image-resize') {
      const { x, y, width } = imageRect(board.images.find((i) => i.id === id)!)
      void settle(() => api.updateImage(id, gesture.kind === 'image' ? { x, y } : { width }))
    } else {
      const { x, y, width, height } = bucketRect(board.buckets.find((b) => b.id === id)!)
      void settle(() => api.updateBucket(id, gesture.kind === 'bucket' ? { x, y } : { width, height }))
    }
  }

  const buckets = board.buckets.map((bucket) => ({ bucket, rect: bucketRect(bucket) }))
  const images = board.images.filter((i) => i.noteId === null).map((image) => ({ image, rect: imageRect(image) }))
  // Most recently updated on top, the note being dragged above everything.
  const notes = board.notes
    .toSorted((a, b) => a.updatedAt.localeCompare(b.updatedAt))
    .map((note) => ({ note, point: notePoint(note) }))
  const draggedNote = dragging?.kind === 'note' ? notes.find((n) => n.note.id === dragging.id) : undefined
  const dropTarget = draggedNote && bucketUnderNote(board.buckets, draggedNote.point)
  const draft =
    gesture?.kind === 'draw' && !gesture.done ? spanRect(snapPoint(gesture.start), snapPoint(gesture.current)) : null

  // The canvas reaches past the furthest item and always fills the viewport.
  const extent = {
    width: Math.max(
      (view.width - RAIL_INSET) / zoom,
      CANVAS_MARGIN +
        Math.max(
          0,
          ...buckets.map((b) => b.rect.x + b.rect.width),
          ...notes.map((n) => n.point.x + NOTE_WIDTH),
          ...images.map((i) => i.rect.x + i.rect.width),
        ),
    ),
    height: Math.max(
      view.height / zoom,
      CANVAS_MARGIN +
        Math.max(
          0,
          ...buckets.map((b) => b.rect.y + b.rect.height),
          ...notes.map((n) => n.point.y + NOTE_HEIGHT),
          ...images.map((i) => i.rect.y + i.rect.height),
        ),
    ),
  }

  const selectedBucket = selection?.kind === 'bucket' ? board.buckets.find((b) => b.id === selection.id) : undefined
  const selectedImage = selection?.kind === 'image' ? board.images.find((i) => i.id === selection.id) : undefined
  const expandedNote = expanded && board.notes.find((n) => n.id === expanded.id)
  const noteToDelete = pendingDelete?.kind === 'note' ? board.notes.find((n) => n.id === pendingDelete.id) : undefined
  const imageToDelete = pendingDelete?.kind === 'image' ? board.images.find((i) => i.id === pendingDelete.id) : undefined
  const countIn = (bucketId: string) => board.notes.filter((n) => n.bucketId === bucketId).length
  const hint = uploads > 0 ? 'Uploading…' : fileOver ? 'Drop to add the image here' : readOnly ? undefined : hints[tool]

  return (
    <section className="board" aria-label={board.name}>
      <div
        className={`board-scroll${fileOver ? ' file-over' : ''}`}
        ref={scrollRef}
        onDragOver={(e) => {
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          e.dataTransfer.dropEffect = 'copy'
          setFileOver(true)
        }}
        onDragLeave={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFileOver(false)
        }}
        onDrop={(e) => {
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          setFileOver(false)
          const at = toCanvas(e)
          void addImages(imageFiles(e.dataTransfer), { at: { x: at.x + IMAGE_OFFSET.x - 20, y: at.y + IMAGE_OFFSET.y - 20 } })
        }}
      >
        <div className="canvas-sizer" style={{ width: extent.width * zoom + RAIL_INSET, height: extent.height * zoom }}>
          <div
            ref={canvasRef}
            className={`canvas tool-${tool}${pan ? ' panning' : ''}`}
            style={{ left: RAIL_INSET, width: extent.width, height: extent.height, transform: `scale(${zoom})` }}
            onPointerDown={onCanvasPointerDown}
            onPointerMove={onCanvasPointerMove}
            onPointerUp={onCanvasPointerUp}
            onPointerCancel={() => {
              setGesture(null)
              setPan(null)
            }}
          >
            {buckets.map(({ bucket, rect }) => {
              const count = countIn(bucket.id)
              return (
                <section
                  key={bucket.id}
                  aria-label={bucket.name}
                  className={[
                    'bucket',
                    dropTarget?.id === bucket.id && 'drop-target',
                    selectedBucket?.id === bucket.id && 'selected',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                  style={{ left: rect.x, top: rect.y, width: rect.width, height: rect.height }}
                >
                  <button
                    type="button"
                    className="bucket-head"
                    title="Drag to move the bucket and its post-its"
                    onPointerDown={(e) => onItemPointerDown(e, 'bucket', bucket.id)}
                    onClick={() => setSelection({ kind: 'bucket', id: bucket.id })}
                  >
                    <span className="bucket-name">{bucket.name}</span>
                    <span className="count" aria-label={count === 1 ? '1 post-it' : `${count} post-its`}>
                      {count}
                    </span>
                  </button>
                  <button
                    type="button"
                    className="bucket-resize"
                    aria-label={`Resize ${bucket.name}`}
                    onPointerDown={(e) => onItemPointerDown(e, 'resize', bucket.id)}
                  >
                    <ResizeIcon />
                  </button>
                </section>
              )
            })}

            {draft && (
              <div
                className={`bucket-draft${draft.width < MIN_BUCKET_WIDTH || draft.height < MIN_BUCKET_HEIGHT ? ' too-small' : ''}`}
                style={{ left: draft.x, top: draft.y, width: draft.width, height: draft.height }}
              />
            )}

            {images.map(({ image, rect }) => (
              <BoardImageView
                key={image.id}
                image={image}
                rect={rect}
                selected={selectedImage?.id === image.id}
                lifted={dragging?.id === image.id}
                onPointerDown={(e) => onItemPointerDown(e, 'image', image.id)}
                onResizePointerDown={(e) => onItemPointerDown(e, 'image-resize', image.id)}
              />
            ))}

            {draggedNote && (
              <div
                className="post-it-slot"
                aria-hidden="true"
                style={{ left: draggedNote.note.x, top: draggedNote.note.y, width: NOTE_WIDTH, height: NOTE_HEIGHT }}
              />
            )}

            {notes.map(({ note, point }) => (
              <PostIt
                key={note.id}
                note={note}
                point={point}
                selected={selection?.kind === 'note' && selection.id === note.id}
                lifted={draggedNote?.note.id === note.id || remoteDrags.some((r) => r.id === note.id && !r.endedAt)}
                onPointerDown={(e) => onItemPointerDown(e, 'note', note.id)}
                onExpand={() => setExpanded({ id: note.id, fresh: false })}
              />
            ))}

            {remoteDrags
              .filter((r) => !r.endedAt && r.id !== dragging?.id)
              .map((r) => {
                const rect = remoteRect(r)
                if (!rect) return null
                const style = { left: rect.x, top: rect.y, width: rect.width, height: rect.height, '--who': presenceColor(r.principalId) }
                return (
                  <div key={r.key} className="remote-drag" aria-hidden="true" style={style as CSSProperties}>
                    <span className="remote-drag-name">{r.displayName}</span>
                  </div>
                )
              })}
          </div>
        </div>
      </div>

      <ToolRail
        tool={tool}
        onToolChange={setTool}
        fill={fill}
        onFillChange={setFill}
        onAddImage={() => void addImagesFromPicker()}
        readOnly={readOnly}
      />
      {readOnly && <div className="read-only-badge">View only</div>}
      {hint && <div className="canvas-hint">{hint}</div>}
      <ViewControls
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={extent}
        viewport={{ x: (view.x - RAIL_INSET) / zoom, y: view.y / zoom, width: view.width / zoom, height: view.height / zoom }}
        buckets={board.buckets}
        notes={board.notes}
        images={images.map((i) => i.image)}
        onNavigate={(center) => {
          const el = scrollRef.current!
          el.scrollLeft = center.x * zoom + RAIL_INSET - el.clientWidth / 2
          el.scrollTop = center.y * zoom - el.clientHeight / 2
        }}
      />

      {selectedBucket && (
        <BucketInspector
          key={selectedBucket.id}
          bucket={selectedBucket}
          noteCount={countIn(selectedBucket.id)}
          autoFocus={!!selection?.fresh}
          onSave={(changes) => settle(() => api.updateBucket(selectedBucket.id, changes))}
          onDelete={() => {
            setSelection(null)
            void settle(() => api.deleteBucket(selectedBucket.id))
          }}
          onClose={() => setSelection(null)}
        />
      )}

      {selectedImage && (
        <ImageInspector
          key={selectedImage.id}
          image={selectedImage}
          onSave={(changes) => settle(() => api.updateImage(selectedImage.id, changes))}
          onDelete={() => setPendingDelete({ kind: 'image', id: selectedImage.id })}
          onClose={() => setSelection(null)}
        />
      )}

      {expandedNote && (
        <ExpandedNote
          key={expandedNote.id}
          note={expandedNote}
          bucket={board.buckets.find((b) => b.id === expandedNote.bucketId)}
          images={board.images.filter((i) => i.noteId === expandedNote.id)}
          focusTitle={expanded?.fresh ?? false}
          onChange={(changes) => settle(() => api.updateNote(expandedNote.id, changes))}
          onDelete={() => deleteNote(expandedNote.id)}
          onAddImages={(files) => void addImages(files, { noteId: expandedNote.id })}
          onPickImages={() => void addImagesFromPicker({ noteId: expandedNote.id })}
          onDeleteImage={(id) => void settle(() => api.deleteImage(id))}
          onClose={() => setExpanded(null)}
          readOnly={readOnly}
        />
      )}

      {noteToDelete && (
        <ConfirmDeleteNote
          title={noteToDelete.title}
          onConfirm={() => deleteNote(noteToDelete.id)}
          onCancel={() => setPendingDelete(null)}
        />
      )}

      {imageToDelete && (
        <ConfirmDialog
          title="Delete this image?"
          message={`"${imageToDelete.caption ?? imageToDelete.fileName}" will be removed from the board. This can't be undone.`}
          confirmLabel="Delete"
          onConfirm={() => deleteImage(imageToDelete.id)}
          onCancel={() => setPendingDelete(null)}
        />
      )}
    </section>
  )
}
