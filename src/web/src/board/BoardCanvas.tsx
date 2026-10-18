import { useEffect, useRef, useState, type PointerEvent } from 'react'
import { api, errorMessage } from '../api/client'
import { bucketColors, type BoardDetail, type BucketDetail, type NoteColor, type NoteDetail, type Point, type Rect } from '../api/types'
import { BucketInspector, NoteInspector } from './Inspector'
import { GripIcon, ResizeIcon } from './icons'
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
import { Toolbar, type Tool } from './Toolbar'

interface Selection {
  kind: 'note' | 'bucket'
  id: string
  /** Just created: the inspector focuses its name so it can be typed straight away. */
  fresh?: boolean
}

/**
 * A pointer gesture on the canvas, in canvas coordinates. A finished gesture (`done`) stays until the board has
 * reloaded, so the dropped item doesn't jump back to its old place while the request is in flight.
 */
type Gesture = GestureTarget & { start: Point; current: Point; done?: boolean }
type GestureTarget = { kind: 'note' | 'bucket' | 'resize'; id: string } | { kind: 'draw' }

/** Pointer travel (px) before a press on a post-it or bucket counts as a drag rather than a click. */
const DRAG_THRESHOLD = 4
/** Empty canvas kept beyond the furthest item, so there is always room to drag things further out. */
const CANVAS_MARGIN = 400

const delta = (g: Gesture): Point => ({ x: g.current.x - g.start.x, y: g.current.y - g.start.y })

function isDrag(g: Gesture) {
  const d = delta(g)
  return Math.abs(d.x) >= DRAG_THRESHOLD || Math.abs(d.y) >= DRAG_THRESHOLD
}

interface BoardCanvasProps {
  boardId: string
  onError: (message: string) => void
}

export function BoardCanvas({ boardId, onError }: BoardCanvasProps) {
  const [board, setBoard] = useState<BoardDetail | null>(null)
  const [tool, setTool] = useState<Tool>('move')
  const [noteColor, setNoteColor] = useState<NoteColor>('Yellow')
  const [selection, setSelection] = useState<Selection | null>(null)
  const [gesture, setGesture] = useState<Gesture | null>(null)
  const canvasRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let ignore = false
    api.getBoard(boardId).then(
      (loaded) => !ignore && setBoard(loaded),
      (error) => onError(errorMessage(error)),
    )
    return () => {
      ignore = true
    }
  }, [boardId, onError])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key !== 'Escape' || (e.target as HTMLElement).closest('input, textarea, select')) return
      setTool('move')
      setSelection(null)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  /** Runs a mutation, then reloads the board so the UI always shows the server's layout. */
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    let ok = true
    try {
      await action()
    } catch (error) {
      onError(errorMessage(error))
      ok = false
    }
    try {
      setBoard(await api.getBoard(boardId))
    } catch (error) {
      onError(errorMessage(error))
    }
    setGesture(null)
    return ok
  }

  if (!board) return <p className="status">Loading board…</p>

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
      const bucket = board!.buckets.find((b) => b.id === note.bucketId)!
      const moved = bucketRect(bucket)
      return { x: Math.max(0, note.x + moved.x - bucket.x), y: Math.max(0, note.y + moved.y - bucket.y) }
    }
    return note
  }

  function toCanvas(e: PointerEvent): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: e.clientX - rect.left, y: e.clientY - rect.top }
  }

  function beginGesture(e: PointerEvent, target: GestureTarget) {
    const point = toCanvas(e)
    canvasRef.current!.setPointerCapture(e.pointerId)
    setGesture({ ...target, start: point, current: point })
  }

  function onItemPointerDown(e: PointerEvent, kind: 'note' | 'bucket' | 'resize', id: string) {
    if (e.button !== 0 || tool !== 'move') return
    e.stopPropagation()
    e.preventDefault()
    setSelection({ kind: kind === 'note' ? 'note' : 'bucket', id })
    beginGesture(e, { kind, id })
  }

  function onCanvasPointerDown(e: PointerEvent) {
    if (e.button !== 0) return
    if (tool === 'bucket') {
      e.preventDefault()
      beginGesture(e, { kind: 'draw' })
    } else if (tool === 'note') {
      const p = toCanvas(e)
      const at = snapPoint({ x: p.x - NOTE_WIDTH / 2, y: p.y - NOTE_HEIGHT / 2 })
      void run(async () => {
        const note = await api.createNote(board!.id, { title: 'New post-it', color: noteColor, ...at })
        setSelection({ kind: 'note', id: note.id, fresh: true })
        setTool('move')
      })
    } else {
      setSelection(null)
    }
  }

  function onCanvasPointerMove(e: PointerEvent) {
    if (gesture && !gesture.done) setGesture({ ...gesture, current: toCanvas(e) })
  }

  function onCanvasPointerUp() {
    if (!gesture || gesture.done) return
    if (gesture.kind === 'draw') {
      const rect = spanRect(snapPoint(gesture.start), snapPoint(gesture.current))
      if (rect.width < MIN_BUCKET_WIDTH || rect.height < MIN_BUCKET_HEIGHT) {
        setGesture(null)
        return
      }
      setGesture({ ...gesture, done: true })
      const color = bucketColors[board!.buckets.length % bucketColors.length]
      void run(async () => {
        const bucket = await api.createBucket(board!.id, { name: 'New bucket', color, ...rect })
        setSelection({ kind: 'bucket', id: bucket.id, fresh: true })
        setTool('move')
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
      const note = board!.notes.find((n) => n.id === id)!
      void run(() => api.moveNote(id, notePoint(note)))
    } else {
      const { x, y, width, height } = bucketRect(board!.buckets.find((b) => b.id === id)!)
      void run(() => api.updateBucket(id, gesture.kind === 'bucket' ? { x, y } : { width, height }))
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

  const extent = {
    width: Math.max(0, ...buckets.map((b) => b.rect.x + b.rect.width), ...notes.map((n) => n.point.x + NOTE_WIDTH)),
    height: Math.max(0, ...buckets.map((b) => b.rect.y + b.rect.height), ...notes.map((n) => n.point.y + NOTE_HEIGHT)),
  }

  const selectedNote = selection?.kind === 'note' ? board.notes.find((n) => n.id === selection.id) : undefined
  const selectedBucket = selection?.kind === 'bucket' ? board.buckets.find((b) => b.id === selection.id) : undefined
  const countIn = (bucketId: string) => board.notes.filter((n) => n.bucketId === bucketId).length
  const looseCount = board.notes.filter((n) => n.bucketId === null).length

  return (
    <section className="board">
      <Toolbar tool={tool} onToolChange={setTool} color={noteColor} onColorChange={setNoteColor} />

      <div className="board-body">
        <div className="board-scroll">
          <div
            ref={canvasRef}
            className={`canvas tool-${tool}`}
            style={{ width: extent.width + CANVAS_MARGIN, height: extent.height + CANVAS_MARGIN }}
            onPointerDown={onCanvasPointerDown}
            onPointerMove={onCanvasPointerMove}
            onPointerUp={onCanvasPointerUp}
            onPointerCancel={() => setGesture(null)}
          >
            {buckets.map(({ bucket, rect }) => (
              <section
                key={bucket.id}
                aria-label={bucket.name}
                className={[
                  'bucket',
                  `bucket-${bucket.color.toLowerCase()}`,
                  dropTarget?.id === bucket.id && 'drop-target',
                  selectedBucket?.id === bucket.id && 'selected',
                ]
                  .filter(Boolean)
                  .join(' ')}
                style={{ left: rect.x, top: rect.y, width: rect.width, height: rect.height }}
              >
                <button
                  type="button"
                  className="bucket-label"
                  title="Drag to move the bucket and its post-its"
                  onPointerDown={(e) => onItemPointerDown(e, 'bucket', bucket.id)}
                  onClick={() => setSelection({ kind: 'bucket', id: bucket.id })}
                >
                  <GripIcon />
                  {bucket.name}
                  <span className="bucket-count">{countIn(bucket.id)}</span>
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
            ))}

            {draft && (
              <div
                className={`bucket-draft${draft.width < MIN_BUCKET_WIDTH || draft.height < MIN_BUCKET_HEIGHT ? ' too-small' : ''}`}
                style={{ left: draft.x, top: draft.y, width: draft.width, height: draft.height }}
              />
            )}

            {notes.map(({ note, point }) => (
              <button
                key={note.id}
                type="button"
                className={[
                  'note',
                  `note-${note.color.toLowerCase()}`,
                  draggedNote?.note.id === note.id && 'lifted',
                  selectedNote?.id === note.id && 'selected',
                ]
                  .filter(Boolean)
                  .join(' ')}
                style={{ left: point.x, top: point.y, width: NOTE_WIDTH, height: NOTE_HEIGHT }}
                onPointerDown={(e) => onItemPointerDown(e, 'note', note.id)}
                onClick={() => setSelection({ kind: 'note', id: note.id })}
              >
                <span className="note-title">{note.title}</span>
                {note.body && <span className="note-body">{note.body}</span>}
              </button>
            ))}
          </div>
        </div>

        {selectedNote && (
          <NoteInspector
            key={selectedNote.id}
            note={selectedNote}
            bucket={board.buckets.find((b) => b.id === selectedNote.bucketId)}
            autoFocus={!!selection?.fresh}
            onSave={(changes) => run(() => api.updateNote(selectedNote.id, changes))}
            onDelete={() => {
              setSelection(null)
              void run(() => api.deleteNote(selectedNote.id))
            }}
            onClose={() => setSelection(null)}
          />
        )}
        {selectedBucket && (
          <BucketInspector
            key={selectedBucket.id}
            bucket={selectedBucket}
            noteCount={countIn(selectedBucket.id)}
            autoFocus={!!selection?.fresh}
            onSave={(changes) => run(() => api.updateBucket(selectedBucket.id, changes))}
            onDelete={() => {
              setSelection(null)
              void run(() => api.deleteBucket(selectedBucket.id))
            }}
            onClose={() => setSelection(null)}
          />
        )}
      </div>

      <footer className="board-footer">
        <span>{board.description}</span>
        <span>
          {board.buckets.length} buckets · {board.notes.length} post-its · {looseCount} loose
        </span>
      </footer>
    </section>
  )
}
