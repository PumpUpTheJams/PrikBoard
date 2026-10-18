import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react'
import { api } from '../api/client'
import type { BoardDetail, BucketDetail, NoteColor, NoteDetail, Point, Rect } from '../api/types'
import { ExpandedNote } from './ExpandedNote'
import { ResizeIcon } from './icons'
import { BucketInspector } from './Inspector'
import {
  MIN_BUCKET_HEIGHT,
  MIN_BUCKET_WIDTH,
  NOTE_HEIGHT,
  NOTE_WIDTH,
  bucketUnderNote,
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
  kind: 'note' | 'bucket'
  id: string
  /** Just created: its name or title is focused so it can be typed straight away. */
  fresh?: boolean
}

/**
 * A pointer gesture on the canvas, in canvas coordinates. A finished gesture (`done`) stays until the board has
 * reloaded, so the dropped item doesn't jump back to its old place while the request is in flight.
 */
type Gesture = GestureTarget & { start: Point; current: Point; done?: boolean }
type GestureTarget = { kind: 'note' | 'bucket' | 'resize'; id: string } | { kind: 'draw' }

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

const hints: Partial<Record<Tool, string>> = {
  pan: 'Drag to look around the board',
  note: 'Click anywhere to stick a post-it',
  bucket: 'Drag across the board to draw a bucket',
}

const delta = (g: Gesture): Point => ({ x: g.current.x - g.start.x, y: g.current.y - g.start.y })

function isDrag(g: Gesture) {
  const d = delta(g)
  return Math.abs(d.x) >= DRAG_THRESHOLD || Math.abs(d.y) >= DRAG_THRESHOLD
}

interface BoardCanvasProps {
  board: BoardDetail
  run: BoardState['run']
  setPaused: BoardState['setPaused']
}

export function BoardCanvas({ board, run, setPaused }: BoardCanvasProps) {
  const [tool, setTool] = useState<Tool>('select')
  const [fill, setFill] = useState<NoteColor>('Butter')
  const [selection, setSelection] = useState<Selection | null>(null)
  const [expanded, setExpanded] = useState<{ id: string; fresh: boolean } | null>(null)
  const [gesture, setGesture] = useState<Gesture | null>(null)
  const [pan, setPan] = useState<Pan | null>(null)
  const [zoom, setZoom] = useState(1)
  /** The scroll viewport, in screen pixels. */
  const [view, setView] = useState<Rect>({ x: 0, y: 0, width: 0, height: 0 })
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  /** A canvas point to keep under the same screen point once a new zoom level has rendered. */
  const zoomAnchor = useRef<{ canvas: Point; screen: Point } | null>(null)

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
      if (expanded || (e.target as HTMLElement).closest('input, textarea, select, [role="menu"]')) return
      if (e.key === 'Escape') {
        setTool('select')
        setSelection(null)
      } else if (!e.ctrlKey && !e.metaKey && !e.altKey && toolKeys[e.key.toLowerCase()]) {
        setTool(toolKeys[e.key.toLowerCase()])
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [expanded])

  /** Runs a change through the board, then lets go of the finished gesture. */
  async function settle(action: () => Promise<unknown>) {
    const ok = await run(action)
    setGesture(null)
    return ok
  }

  // Live geometry: the server's layout, with the gesture in progress applied on top.
  const dragging = gesture && gesture.kind !== 'draw' && isDrag(gesture) ? gesture : null

  function bucketRect(bucket: BucketDetail): Rect {
    if (dragging?.id !== bucket.id) return bucket
    const d = delta(dragging)
    if (dragging.kind === 'bucket') return { ...bucket, ...snapPoint({ x: bucket.x + d.x, y: bucket.y + d.y }) }
    if (dragging.kind === 'resize') {
      return {
        ...bucket,
        width: Math.max(MIN_BUCKET_WIDTH, snap(bucket.width + d.x)),
        height: Math.max(MIN_BUCKET_HEIGHT, snap(bucket.height + d.y)),
      }
    }
    return bucket
  }

  function notePoint(note: NoteDetail): Point {
    if (!dragging) return note
    if (dragging.kind === 'note' && dragging.id === note.id) {
      const d = delta(dragging)
      return snapPoint({ x: note.x + d.x, y: note.y + d.y })
    }
    if (dragging.kind === 'bucket' && dragging.id === note.bucketId) {
      // A moving bucket carries its notes along, as the server does.
      const bucket = board.buckets.find((b) => b.id === note.bucketId)!
      const moved = bucketRect(bucket)
      return { x: Math.max(0, note.x + moved.x - bucket.x), y: Math.max(0, note.y + moved.y - bucket.y) }
    }
    return note
  }

  function toCanvas(e: PointerEvent): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: (e.clientX - rect.left) / zoom, y: (e.clientY - rect.top) / zoom }
  }

  function beginGesture(e: PointerEvent, target: GestureTarget) {
    const point = toCanvas(e)
    canvasRef.current!.setPointerCapture(e.pointerId)
    setGesture({ ...target, start: point, current: point })
  }

  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: 'note' | 'bucket' | 'resize', id: string) {
    if (e.button !== 0 || tool !== 'select') return
    e.stopPropagation()
    e.preventDefault()
    if (kind === 'note') e.currentTarget.focus({ preventScroll: true })
    setSelection({ kind: kind === 'note' ? 'note' : 'bucket', id })
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
    } else {
      const { x, y, width, height } = bucketRect(board.buckets.find((b) => b.id === id)!)
      void settle(() => api.updateBucket(id, gesture.kind === 'bucket' ? { x, y } : { width, height }))
    }
  }

  const buckets = board.buckets.map((bucket) => ({ bucket, rect: bucketRect(bucket) }))
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
      CANVAS_MARGIN + Math.max(0, ...buckets.map((b) => b.rect.x + b.rect.width), ...notes.map((n) => n.point.x + NOTE_WIDTH)),
    ),
    height: Math.max(
      view.height / zoom,
      CANVAS_MARGIN + Math.max(0, ...buckets.map((b) => b.rect.y + b.rect.height), ...notes.map((n) => n.point.y + NOTE_HEIGHT)),
    ),
  }

  const selectedBucket = selection?.kind === 'bucket' ? board.buckets.find((b) => b.id === selection.id) : undefined
  const expandedNote = expanded && board.notes.find((n) => n.id === expanded.id)
  const countIn = (bucketId: string) => board.notes.filter((n) => n.bucketId === bucketId).length
  const hint = hints[tool]

  return (
    <section className="board" aria-label={board.name}>
      <div className="board-scroll" ref={scrollRef}>
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
                lifted={draggedNote?.note.id === note.id}
                onPointerDown={(e) => onItemPointerDown(e, 'note', note.id)}
                onExpand={() => setExpanded({ id: note.id, fresh: false })}
              />
            ))}
          </div>
        </div>
      </div>

      <ToolRail tool={tool} onToolChange={setTool} fill={fill} onFillChange={setFill} />
      {hint && <div className="canvas-hint">{hint}</div>}
      <ViewControls
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={extent}
        viewport={{ x: (view.x - RAIL_INSET) / zoom, y: view.y / zoom, width: view.width / zoom, height: view.height / zoom }}
        buckets={board.buckets}
        notes={board.notes}
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

      {expandedNote && (
        <ExpandedNote
          key={expandedNote.id}
          note={expandedNote}
          bucket={board.buckets.find((b) => b.id === expandedNote.bucketId)}
          focusTitle={expanded?.fresh ?? false}
          onChange={(changes) => settle(() => api.updateNote(expandedNote.id, changes))}
          onDelete={() => {
            if (!window.confirm(`Delete "${expandedNote.title}"?`)) return
            setExpanded(null)
            setSelection(null)
            void settle(() => api.deleteNote(expandedNote.id))
          }}
          onClose={() => setExpanded(null)}
        />
      )}
    </section>
  )
}
