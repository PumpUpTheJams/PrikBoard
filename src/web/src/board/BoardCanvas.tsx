import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type PointerEvent } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../api/client'
import type { BoardDetail, BoardTag, BucketDetail, DragKind, DragUpdate, ImageDetail, NoteColor, NoteDetail, Point, Rect, ResizeHandle } from '../api/types'
import { presenceColor } from '../live/presence'
import type { LiveDrag } from '../live/useLive'
import { ConfirmDeleteNote, ConfirmDialog } from '../components/ConfirmDialog'
import { Toast, type ToastMessage } from '../components/Toast'
import { AssigneePicker } from './AssigneePicker'
import { BoardImageView } from './BoardImageView'
import { ExpandedNote, type ExpandedNoteHandle } from './ExpandedNote'
import { draggingFiles, imageFiles, pickImageFiles } from './files'
import { delta, isDrag } from './gesture'
import { noteIdFromHash, notePath, noteRoute } from './links'
import { pop, reducedMotion } from './motion'
import { BucketInspector, ImageInspector } from './Inspector'
import {
  GRID,
  MIN_BUCKET_HEIGHT,
  MIN_BUCKET_WIDTH,
  NOTE_HEIGHT,
  NOTE_WIDTH,
  bucketSize,
  bucketUnderNote,
  defaultBlockSize,
  onNoteCanvas,
  resizeFrom,
  resizeImage,
  snapPoint,
  spanRect,
} from './layout'
import { PostIt } from './PostIt'
import { resizeCursor } from './resizeCursor'
import { ResizeHandles } from './ResizeHandles'
import { TagFilter } from './TagFilter'
import { TagPicker } from './TagPicker'
import { ToolRail } from './ToolRail'
import { toolKeys, type Tool } from './tools'
import type { IncomingImage } from './NoteCanvas'
import type { BoardState } from './useBoard'
import { useZoom } from './useZoom'
import { ViewControls } from './ViewControls'
import { stepZoom } from './zoom'

// Loaded on first open: the Markdown renderer is the biggest part of it.

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
/** `handle`: for resizes, the corner or edge being dragged. */
type GestureTarget = { kind: ItemGesture; id: string; handle?: ResizeHandle } | { kind: 'draw' }

/**
 * Where a dragged post-it would be linked on release: the open post-it's window (`window`; `at` is the point on its
 * canvas, when over the canvas) or another post-it on the board.
 */
interface LinkDrop {
  note: NoteDetail
  window: boolean
  at?: Point
  /** The pointer, in screen pixels. */
  client: Point
}

/** A pan in progress: where the pointer went down and the scroll position at that moment, in screen pixels. */
interface Pan {
  x: number
  y: number
  left: number
  top: number
}

/** Empty canvas kept beyond the furthest item, so there is always room to drag things further out. */
const CANVAS_MARGIN = 600
/** Screen space left of the canvas, so the tool rail doesn't cover the left edge of the board. */
const RAIL_INSET = 72
/** Where a new image's top-left goes relative to the point it was dropped, pasted or added at. */
const IMAGE_OFFSET = { x: 180, y: 120 }

const hints: Partial<Record<Tool, string>> = {
  note: 'Click anywhere to stick a post-it',
  bucket: 'Drag across the board to draw a bucket',
}

/** A drag applied on top of the server's layout: your own gesture, or someone else's relayed by live updates. */
interface ActiveDrag {
  kind: ItemGesture
  id: string
  d: Point
  handle: ResizeHandle
}

const fromDragKind: Record<DragKind, ItemGesture> = {
  Note: 'note',
  Bucket: 'bucket',
  Resize: 'resize',
  Image: 'image',
  ImageResize: 'image-resize',
}
const toDragKind = Object.fromEntries(Object.entries(fromDragKind).map(([k, v]) => [v, k])) as Record<ItemGesture, DragKind>

/** Where in a post-it's title text a screen point is, as a character offset; undefined when it isn't on the text. */
function caretAt(x: number, y: number): number | undefined {
  const position = document.caretPositionFromPoint?.(x, y)
  const node = position?.offsetNode ?? document.caretRangeFromPoint?.(x, y)?.startContainer
  const offset = position?.offset ?? document.caretRangeFromPoint?.(x, y)?.startOffset
  return node?.parentElement?.closest('.post-it-title') ? offset : undefined
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
  /** The opened post-it. A link to a post-it (/#/boards/{id}/notes/{id}) opens it when the board loads. */
  const [expanded, setExpanded] = useState<{ id: string; fresh: boolean } | null>(() => {
    const id = noteIdFromHash()
    return id ? { id, fresh: false } : null
  })
  /** Something waiting for the person to confirm its deletion (Delete key on the board, or the image panel). */
  const [pendingDelete, setPendingDelete] = useState<{ kind: 'note' | 'image'; id: string } | null>(null)
  /** The tag or people window opened from a post-it on the board, next to the part that was clicked (screen pixels). */
  const [quickEdit, setQuickEdit] = useState<{ noteId: string; kind: 'tags' | 'people'; anchor: DOMRect } | null>(null)
  /** Tags to filter on: post-its with none of them fade. Not kept: every board starts unfiltered. */
  const [tagFilter, setTagFilter] = useState<string[]>([])
  /** The post-it whose title is being edited in place, and where the text cursor starts (default: the end). */
  const [titleEdit, setTitleEdit] = useState<{ id: string; caret?: number } | null>(null)
  /** The post-it whose title the current press started on: a click there (no drag) edits the title. */
  const titlePress = useRef<string | null>(null)
  /**
   * The post-its the last two presses were on (null: something else). The canvas holds the pointer during a press, so the
   * browser sends clicks and double-clicks to the canvas, not to the post-it; a double-click on one post-it expands it.
   */
  const notePresses = useRef<[string | null, string | null]>([null, null])
  const [gesture, setGesture] = useState<Gesture | null>(null)
  const [pan, setPan] = useState<Pan | null>(null)
  /** A drag in progress on the open post-it's canvas. */
  const [noteBusy, setNoteBusy] = useState(false)
  /** A board image being dragged over the open post-it, to attach it there. */
  const [incoming, setIncoming] = useState<IncomingImage | null>(null)
  /** Where a post-it being dragged would be linked, instead of moved. */
  const [linkDrop, setLinkDrop] = useState<LinkDrop | null>(null)
  /** A new post-it being dragged from the tool rail: the pointer, in screen pixels. */
  const [newPostIt, setNewPostIt] = useState<Point | null>(null)
  const expandedRef = useRef<ExpandedNoteHandle>(null)
  /** Image uploads in flight, and whether files are being dragged over the board. */
  const [uploads, setUploads] = useState(0)
  const [fileOver, setFileOver] = useState(false)
  /** The scroll viewport, in screen pixels. */
  const [view, setView] = useState<Rect>({ x: 0, y: 0, width: 0, height: 0 })
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  /**
   * Empty space around the board, half the viewport on every side (screen pixels): zoomed out, or near an edge, the
   * board can still be scrolled so any part of it, its top-left corner too, sits in the middle of the screen.
   */
  const margin = { x: Math.round(view.width / 2), y: Math.round(view.height / 2) }
  /** Where the canvas's top-left corner is in the scrolled area: the margin, and the tool rail's room on the left. */
  const origin = { x: margin.x + RAIL_INSET, y: margin.y }
  // The wheel zooms the board (Shift + wheel scrolls). A quick-edit window sits next to the post-it on screen; after
  // zooming it would point at nothing.
  const { zoom, zoomTo } = useZoom(scrollRef, origin, { onZoom: () => setQuickEdit(null), wheel: 'always' })
  /** A short confirmation at the bottom of the board (a link was added, an image went into a post-it). */
  const [toast, setToast] = useState<ToastMessage | null>(null)
  const clearToast = useCallback(() => setToast(null), [])
  const notify = (text: string) => setToast({ id: Date.now(), text })
  /** Last pointer position over the canvas, in canvas pixels: where pasted images go. */
  const lastPointer = useRef<Point | null>(null)

  // The address bar follows the opened post-it, so it can be bookmarked or shared. Replacing the entry doesn't fire
  // hashchange; a post-it that isn't (or no longer is) on the board drops out of the address.
  const openNoteId = expanded && board.notes.some((n) => n.id === expanded.id) ? expanded.id : null
  useEffect(() => {
    const hash = openNoteId ? notePath({ boardId: board.id, noteId: openNoteId }).slice(1) : `#/boards/${board.id}`
    if (window.location.hash !== hash) history.replaceState(history.state, '', hash)
  }, [openNoteId, board.id])

  // Following a link to another post-it on this board opens it (one on another board loads that board: App).
  useEffect(() => {
    function onHashChange() {
      if (!window.location.hash.startsWith(`#/boards/${board.id}`)) return
      const id = noteIdFromHash()
      setTitleEdit(null)
      setQuickEdit(null)
      setExpanded(id ? { id, fresh: false } : null)
      if (id) setSelection({ kind: 'note', id })
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [board.id])

  // The margin follows the viewport's size. When it changes (the first measurement, a resized window), the scroll
  // position moves by as much, so what is on screen stays where it is; a board opens with its top-left corner in view.
  const lastMargin = useRef({ x: 0, y: 0 })
  useLayoutEffect(() => {
    const el = scrollRef.current!
    el.scrollLeft += margin.x - lastMargin.current.x
    el.scrollTop += margin.y - lastMargin.current.y
    lastMargin.current = { x: margin.x, y: margin.y }
  }, [margin.x, margin.y])

  // Background refreshes would fight with a drag in progress.
  useEffect(() => setPaused(gesture !== null || noteBusy), [gesture, noteBusy, setPaused])

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

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      // The open post-it is a window of its own: keys typed in it are its own, and Escape closes it.
      if (pendingDelete) return
      if (e.target instanceof Element && e.target.closest('input, textarea, select, [role="menu"], [role="dialog"], [role="alertdialog"]')) return
      if (e.key === 'Escape') {
        if (expanded) return
        setTool('select')
        setSelection(null)
      } else if (readOnly) {
        if (e.key.toLowerCase() === 'v') setTool('select')
      } else if ((e.key === 'Delete' || e.key === 'Backspace') && (selection?.kind === 'note' || selection?.kind === 'image')) {
        e.preventDefault()
        setPendingDelete({ kind: selection.kind, id: selection.id })
      } else if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'd' && selection?.kind === 'note') {
        e.preventDefault()
        if (!e.repeat) duplicateNote(selection.id)
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

  // Pasting an image puts it on the board where the pointer was, or on the open post-it when the pointer or focus is there.
  useEffect(() => {
    function onPaste(e: ClipboardEvent) {
      const files = imageFiles(e.clipboardData)
      if (readOnly || files.length === 0) return
      e.preventDefault()
      const note = expandedRef.current
      void addImages(
        files,
        expanded && note?.wantsPaste() ? { noteId: expanded.id, at: note.pastePoint() } : { at: lastPointer.current ?? undefined },
      )
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

  /** Copies a post-it's content one grid step down and right of it, and selects the copy. Attached images stay behind. */
  function duplicateNote(id: string) {
    const note = board.notes.find((n) => n.id === id)
    if (!note) return
    void settle(async () => {
      const copy = await api.createNote(board.id, {
        title: note.title,
        color: note.color,
        tags: note.tags,
        // People who have since left the board stay on the original, but can't be put on a new post-it.
        assigneeIds: note.assignees.map((a) => a.principalId).filter((id) => board.people.some((p) => p.principalId === id)),
        blocks: note.blocks.map(({ kind, title, text, items, links, x, y, width, height }) => ({ kind, title, text, items, links, x, y, width, height })),
        ...snapPoint({ x: note.x + GRID, y: note.y + GRID }),
      })
      setSelection({ kind: 'note', id: copy.id })
    })
  }

  const renameTag = (tag: BoardTag, name: string) => settle(() => api.renameBoardTag(board.id, tag.id, name))
  const deleteTag = (tag: BoardTag) => settle(() => api.deleteBoardTag(board.id, tag.id))

  function deleteImage(id: string) {
    setPendingDelete(null)
    if (selection?.id === id) setSelection(null)
    void settle(() => api.deleteImage(id))
  }

  /** The middle of what is on screen, in canvas pixels. */
  function viewportCenter(): Point {
    return { x: (view.x - origin.x + view.width / 2) / zoom, y: (view.y - origin.y + view.height / 2) / zoom }
  }

  /**
   * Uploads images one after another, cascading so they don't cover each other: onto the board around `at` (default:
   * the middle of the screen), or attached to a note with their top-left at `at` on its canvas (default: a free spot).
   * A single image on the board is selected afterwards.
   */
  async function addImages(files: File[], target: { at?: Point; noteId?: string }) {
    if (files.length === 0) return
    const at = target.at ?? viewportCenter()
    setUploads((n) => n + 1)
    await run(async () => {
      let last: ImageDetail | undefined
      for (const [i, file] of files.entries()) {
        const placement = target.noteId
          ? { noteId: target.noteId, ...(target.at ? snapPoint({ x: target.at.x + i * 40, y: target.at.y + i * 40 }) : {}) }
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
    ...(dragging ? [{ kind: dragging.kind, id: dragging.id, d: delta(dragging), handle: dragging.handle ?? 'BottomRight' }] : []),
    ...remoteDrags.map((r) => ({ kind: fromDragKind[r.kind], id: r.id, d: { x: r.dx, y: r.dy }, handle: r.handle ?? 'BottomRight' })),
  ]
  const dragOf = (id: string | null) => (id === null ? undefined : drags.find((g) => g.id === id))

  // Tell the others about your drag while it is in progress (as a string, so an unchanged drag sends nothing).
  const sentDrag =
    dragging && !dragging.done
      ? JSON.stringify({ kind: toDragKind[dragging.kind], id: dragging.id, dx: delta(dragging).x, dy: delta(dragging).y, handle: dragging.handle })
      : null
  useEffect(() => onDrag?.(sentDrag ? (JSON.parse(sentDrag) as DragUpdate) : null), [sentDrag, onDrag])

  function bucketRect(bucket: BucketDetail): Rect {
    const drag = dragOf(bucket.id)
    if (!drag) return bucket
    const { d } = drag
    if (drag.kind === 'bucket') return { ...bucket, ...snapPoint({ x: bucket.x + d.x, y: bucket.y + d.y }) }
    if (drag.kind === 'resize') return { ...bucket, ...resizeFrom(bucket, drag.handle, d, bucketSize) }
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
    if (drag.kind === 'image-resize') {
      const resized = resizeImage(image, drag.handle, d)
      return { ...image, ...resized, ...snapPoint(resized) }
    }
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

  /** A screen point in canvas pixels when it is over the board (and not over the open post-it); else null. */
  function boardPoint(client: Point): Point | null {
    const rect = scrollRef.current!.getBoundingClientRect()
    const inside = client.x >= rect.left && client.x < rect.right && client.y >= rect.top && client.y < rect.bottom
    return inside ? toCanvas({ clientX: client.x, clientY: client.y }) : null
  }

  /** The topmost post-it on the board under a canvas point, other than `except`. */
  function noteAt(p: Point, except?: string): NoteDetail | undefined {
    return board.notes
      .toSorted((a, b) => a.updatedAt.localeCompare(b.updatedAt))
      .findLast((n) => n.id !== except && p.x >= n.x && p.x < n.x + NOTE_WIDTH && p.y >= n.y && p.y < n.y + NOTE_HEIGHT)
  }

  /**
   * Where a post-it being dragged would be linked when released at a screen point: the open post-it (anywhere over its
   * window), or the post-it under the pointer on the board. Null for other drags, or over nothing to link to.
   */
  function linkDropFor(g: Gesture, client: Point): LinkDrop | null {
    if (g.kind !== 'note') return null
    const win = expandedRef.current
    if (expanded && win?.contains(client)) {
      const open = expanded.id !== g.id ? board.notes.find((n) => n.id === expanded.id) : undefined
      return open ? { note: open, window: true, at: win.hits(client) ? win.toNote(client) : undefined, client } : null
    }
    const under = noteAt(toCanvas({ clientX: client.x, clientY: client.y }), g.id)
    return under ? { note: under, window: false, client } : null
  }

  /**
   * Adds a link to `linked` on a post-it: into the links block at `at` on its canvas, else its first links block; only a
   * post-it without one gets a new links block (at `at`, or in a free spot). Nothing changes (false) when it already
   * links there.
   */
  async function linkNote({ note, at }: LinkDrop, linked: NoteDetail) {
    if (note.blocks.some((b) => b.links?.some((l) => noteRoute(l.url)?.noteId === linked.id))) return false
    const link = { title: linked.title, url: linked.path }
    const lists = note.blocks.filter((b) => b.kind === 'Links')
    const block =
      (at && lists.find((b) => at.x >= b.x && at.x < b.x + b.width && at.y >= b.y && at.y < b.y + b.height)) ?? lists[0]
    if (block) await api.updateNoteBlock(note.id, block.id, { links: [...(block.links ?? []), link] })
    else {
      const place = at ? onNoteCanvas({ x: at.x - GRID, y: at.y - GRID }, defaultBlockSize('Links')) : {}
      await api.addNoteBlock(note.id, { kind: 'Links', links: [link], ...place })
    }
    return true
  }

  const postItElement = (id: string) => canvasRef.current?.querySelector(`[data-note="${id}"]`)

  /**
   * An image dropped on a post-it on the board is sucked into it: it shrinks into the post-it's middle and fades, then
   * the post-it gives a little gulp. The image stays hidden (until the reload takes it off the board) unless cancelled.
   */
  function suckInto(imageId: string, note: NoteDetail): Animation | undefined {
    const el = canvasRef.current?.querySelector(`[data-image="${imageId}"]`)
    const image = board.images.find((i) => i.id === imageId)
    if (!el || !image) return undefined
    const from = imageRect(image)
    const to = notePoint(note)
    const dx = to.x + NOTE_WIDTH / 2 - (from.x + from.width / 2)
    const dy = to.y + NOTE_HEIGHT / 2 - (from.y + from.height / 2)
    const keyframes = reducedMotion()
      ? [{ opacity: 0.6 }, { opacity: 0 }]
      : [
          { transform: 'translate(0, 0) scale(1)', opacity: 0.6 },
          { transform: `translate(${dx * 0.7}px, ${dy * 0.7}px) scale(0.35)`, opacity: 0.5, offset: 0.6 },
          { transform: `translate(${dx}px, ${dy}px) scale(0.02)`, opacity: 0 },
        ]
    const animation = el.animate(keyframes, { duration: 420, easing: 'cubic-bezier(0.5, 0, 0.75, 0)', fill: 'forwards' })
    animation.onfinish = () => pop(postItElement(note.id), 1.06)
    return animation
  }

  /**
   * Where a board image being dragged would land on the open post-it's canvas (keeping the point it was grabbed at
   * under the pointer), or null when the pointer is not over it.
   */
  function dropOnNote(g: Gesture, client: Point): IncomingImage | null {
    const note = expandedRef.current
    if (g.kind !== 'image' || !note?.hits(client)) return null
    const image = board.images.find((i) => i.id === g.id)
    if (!image) return null
    const at = note.toNote(client)
    return { image, at: { x: at.x - (g.start.x - image.x), y: at.y - (g.start.y - image.y) } }
  }

  function expand(id: string) {
    setTitleEdit(null)
    setQuickEdit(null)
    setExpanded({ id, fresh: false })
  }

  function beginGesture(e: PointerEvent, target: GestureTarget) {
    setQuickEdit(null)
    const point = toCanvas(e)
    canvasRef.current!.setPointerCapture(e.pointerId)
    setGesture({ ...target, start: point, current: point })
  }

  /**
   * A press on an item: selects it and starts dragging it (`handle`: resizing from that corner or edge). With `move` false it
   * only selects, as a press inside a bucket that isn't selected yet does.
   */
  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: ItemGesture, id: string, { handle, move = true }: { handle?: ResizeHandle; move?: boolean } = {}) {
    if (e.button !== 0 || tool !== 'select') return
    notePresses.current = [notePresses.current[1], kind === 'note' ? id : null]
    e.stopPropagation()
    e.preventDefault()
    // preventDefault keeps focus where it is, so let a title being edited go (it saves on blur).
    if (titleEdit && document.activeElement instanceof HTMLElement) document.activeElement.blur()
    if (readOnly) {
      // Post-its can still be selected (and opened with Enter); nothing moves and no panels open.
      if (kind === 'note') {
        e.currentTarget.focus({ preventScroll: true })
        setSelection({ kind: 'note', id })
      }
      return
    }
    if (kind === 'note' || kind === 'image') e.currentTarget.focus({ preventScroll: true })
    const onTitle = e.target instanceof Element && e.target.closest('.post-it-title') !== null
    titlePress.current = kind === 'note' && onTitle ? id : null
    setSelection({ kind: kind === 'note' ? 'note' : kind === 'image' || kind === 'image-resize' ? 'image' : 'bucket', id })
    if (move) beginGesture(e, { kind, id, handle })
  }

  function onCanvasPointerDown(e: PointerEvent) {
    notePresses.current = [notePresses.current[1], null]
    // The middle button pans (the wheel zooms, Shift + wheel scrolls).
    if (e.button === 1) {
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
      stickPostIt(toCanvas(e))
    } else {
      setSelection(null)
    }
  }

  /** Where a new post-it centred on a canvas point goes: snapped to the grid. */
  const postItSpot = (p: Point) => snapPoint({ x: p.x - NOTE_WIDTH / 2, y: p.y - NOTE_HEIGHT / 2 })

  /** Sticks a new post-it centred on a canvas point, selects it and opens it with its title ready to type. */
  function stickPostIt(p: Point) {
    void settle(async () => {
      const note = await api.createNote(board.id, { title: 'New post-it', color: fill, ...postItSpot(p) })
      setSelection({ kind: 'note', id: note.id })
      setExpanded({ id: note.id, fresh: true })
      setTool('select')
    })
  }

  /** Where a new post-it dragged from the rail would go, in canvas pixels: over the board, not over the open post-it. */
  function newPostItPoint(client: Point): Point | null {
    if (expandedRef.current?.contains(client)) return null
    return boardPoint(client)
  }

  /**
   * A press on the rail's post-it button. Dragging from there carries a new post-it, which is stuck where it is released
   * (over a bucket, it lands in that bucket). Without a drag it stays a click, which picks the post-it tool. Escape, or
   * releasing off the board, drops it.
   */
  function onPostItPointerDown(e: PointerEvent<HTMLButtonElement>) {
    if (e.button !== 0 || readOnly) return
    e.preventDefault()
    const start = { x: e.clientX, y: e.clientY }
    let carried = false
    const end = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', end)
      window.removeEventListener('keydown', onKey, true)
      setNewPostIt(null)
    }
    const onMove = (ev: globalThis.PointerEvent) => {
      const current = { x: ev.clientX, y: ev.clientY }
      if (!carried && !isDrag({ start, current })) return
      carried = true
      setQuickEdit(null)
      setNewPostIt(current)
    }
    const onUp = (ev: globalThis.PointerEvent) => {
      end()
      if (!carried) return
      // A drag isn't a click: the click that may follow on the button doesn't pick the tool.
      const swallow = (c: MouseEvent) => c.stopPropagation()
      window.addEventListener('click', swallow, { capture: true, once: true })
      setTimeout(() => window.removeEventListener('click', swallow, true))
      const at = newPostItPoint({ x: ev.clientX, y: ev.clientY })
      if (at) stickPostIt(at)
    }
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key !== 'Escape') return
      ev.stopPropagation()
      end()
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', end)
    window.addEventListener('keydown', onKey, true)
  }

  function onCanvasPointerMove(e: PointerEvent) {
    lastPointer.current = toCanvas(e)
    if (pan) {
      const el = scrollRef.current!
      el.scrollLeft = pan.left - (e.clientX - pan.x)
      el.scrollTop = pan.top - (e.clientY - pan.y)
    } else if (gesture && !gesture.done) {
      const next = { ...gesture, current: toCanvas(e) }
      setGesture(next)
      setIncoming(isDrag(next) ? dropOnNote(next, { x: e.clientX, y: e.clientY }) : null)
      setLinkDrop(isDrag(next) ? linkDropFor(next, { x: e.clientX, y: e.clientY }) : null)
    }
  }

  function onCanvasPointerUp(e: PointerEvent) {
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
      // A click on a post-it's title edits it, with the text cursor where the click was.
      const id = titlePress.current
      if (gesture.kind === 'note' && id === gesture.id) setTitleEdit({ id, caret: caretAt(e.clientX, e.clientY) })
      return
    }

    const client = { x: e.clientX, y: e.clientY }
    const link = linkDropFor(gesture, client)
    setLinkDrop(null)
    if (link) {
      // Dropped on another post-it (or the open one's window): that one links to it, and the dragged one stays put.
      setGesture(null)
      const linked = board.notes.find((n) => n.id === gesture.id)!
      let created = false
      void run(async () => (created = await linkNote(link, linked))).then((ok) => {
        if (!ok) return
        if (!created) return notify('Already linked')
        // Both post-its pop: the one dragged (back in its place) and the one that got the link.
        pop(postItElement(linked.id))
        if (link.window) expandedRef.current?.pop()
        else pop(postItElement(link.note.id))
        notify('Link created')
      })
      return
    }

    setGesture({ ...gesture, done: true })
    const { id } = gesture
    const onNote = expanded && dropOnNote(gesture, client)
    setIncoming(null)
    const overWindow = !!expandedRef.current?.contains(client)
    const intoNote = gesture.kind === 'image' && !onNote && !overWindow ? noteAt(toCanvas(e)) : undefined
    if (expanded && onNote) {
      // Dropped on the open post-it: the image moves onto its canvas.
      setSelection(null)
      void settle(() => api.updateImage(id, { noteId: expanded.id, ...onNoteCanvas(onNote.at, onNote.image) })).then(
        (ok) => ok && notify('Image embedded'),
      )
    } else if (intoNote) {
      // Dropped on a post-it on the board: sucked into it and attached, in a free spot on its canvas, without opening it.
      // The request waits for the animation, so the reload doesn't take the image away halfway.
      setSelection(null)
      const animation = suckInto(id, intoNote)
      void settle(async () => {
        await animation?.finished.catch(() => {})
        await api.updateImage(id, { noteId: intoNote.id })
      }).then((ok) => (ok ? notify('Image embedded') : animation?.cancel()))
    } else if (gesture.kind === 'note') {
      const note = board.notes.find((n) => n.id === id)!
      void settle(() => api.moveNote(id, notePoint(note)))
    } else if (gesture.kind === 'image' || gesture.kind === 'image-resize') {
      const { x, y, width } = imageRect(board.images.find((i) => i.id === id)!)
      void settle(() => api.updateImage(id, gesture.kind === 'image' ? { x, y } : { x, y, width }))
    } else {
      // With a new size, a new place is a resize from a top or left corner or edge: the server leaves the post-its where they are.
      const { x, y, width, height } = bucketRect(board.buckets.find((b) => b.id === id)!)
      void settle(() => api.updateBucket(id, gesture.kind === 'bucket' ? { x, y } : { x, y, width, height }))
    }
  }

  const buckets = board.buckets.map((bucket) => ({ bucket, rect: bucketRect(bucket) }))
  const images = board.images.filter((i) => i.noteId === null).map((image) => ({ image, rect: imageRect(image) }))
  // Most recently updated on top, the note being dragged above everything.
  const notes = board.notes
    .toSorted((a, b) => a.updatedAt.localeCompare(b.updatedAt))
    .map((note) => ({ note, point: notePoint(note) }))
  const resizing = gesture && (gesture.kind === 'resize' || gesture.kind === 'image-resize') && !gesture.done ? gesture : null
  const draggedNote = dragging?.kind === 'note' ? notes.find((n) => n.note.id === dragging.id) : undefined
  /** The post-it a dragged image would be attached to (not while it is over the open post-it's window). */
  const imageTarget = dragging?.kind === 'image' && !dragging.done && !incoming ? noteAt(dragging.current) : undefined
  /** Where a new post-it carried from the rail would land, on the canvas. */
  const newPostItAt = newPostIt && newPostItPoint(newPostIt)
  const newPostItSpot = newPostItAt ? postItSpot(newPostItAt) : null
  const dropTarget =
    draggedNote && !linkDrop
      ? bucketUnderNote(board.buckets, draggedNote.point)
      : newPostItSpot
        ? bucketUnderNote(board.buckets, newPostItSpot)
        : undefined
  const draft =
    gesture?.kind === 'draw' && !gesture.done ? spanRect(snapPoint(gesture.start), snapPoint(gesture.current)) : null

  // The canvas reaches past the furthest item; the margin around it fills the rest of the viewport.
  const extent = {
    width:
      CANVAS_MARGIN +
      Math.max(
        0,
        ...buckets.map((b) => b.rect.x + b.rect.width),
        ...notes.map((n) => n.point.x + NOTE_WIDTH),
        ...images.map((i) => i.rect.x + i.rect.width),
      ),
    height:
      CANVAS_MARGIN +
      Math.max(
        0,
        ...buckets.map((b) => b.rect.y + b.rect.height),
        ...notes.map((n) => n.point.y + NOTE_HEIGHT),
        ...images.map((i) => i.rect.y + i.rect.height),
      ),
  }

  const selectedBucket = selection?.kind === 'bucket' ? board.buckets.find((b) => b.id === selection.id) : undefined
  const selectedImage = selection?.kind === 'image' ? board.images.find((i) => i.id === selection.id) : undefined
  const expandedNote = expanded && board.notes.find((n) => n.id === expanded.id)
  const noteToDelete = pendingDelete?.kind === 'note' ? board.notes.find((n) => n.id === pendingDelete.id) : undefined
  const imageToDelete = pendingDelete?.kind === 'image' ? board.images.find((i) => i.id === pendingDelete.id) : undefined
  const countIn = (bucketId: string) => board.notes.filter((n) => n.bucketId === bucketId).length
  // Filter on tags that still exist (one may have been renamed or deleted meanwhile); any of them matches.
  const filter = tagFilter.filter((t) => board.tags.some((bt) => bt.name === t))
  const dimmed = (note: NoteDetail) => filter.length > 0 && !note.tags.some((t) => filter.includes(t))
  const quickNote = quickEdit && board.notes.find((n) => n.id === quickEdit.noteId)
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
        <div
          className="canvas-sizer"
          style={{ width: origin.x + extent.width * zoom + margin.x, height: origin.y + extent.height * zoom + margin.y }}
        >
          <div
            ref={canvasRef}
            className={`canvas tool-${tool}${pan ? ' panning' : ''}`}
            style={{
              left: origin.x,
              top: origin.y,
              width: extent.width,
              height: extent.height,
              transform: `scale(${zoom})`,
              // The canvas's dots and presses reach over the margin too (in canvas pixels: the canvas is scaled).
              ...({
                '--margin-left': `${origin.x / zoom}px`,
                '--margin-top': `${origin.y / zoom}px`,
                '--margin-right': `${margin.x / zoom}px`,
                '--margin-bottom': `${margin.y / zoom}px`,
              } as CSSProperties),
              // The canvas holds the pointer during a resize, so it shows the handle's cursor.
              cursor: resizing?.handle ? resizeCursor[resizing.handle] : undefined,
            }}
            onPointerDown={onCanvasPointerDown}
            onDoubleClick={() => {
              const [first, second] = notePresses.current
              if (second && first === second) expand(second)
            }}
            onPointerMove={onCanvasPointerMove}
            onPointerUp={onCanvasPointerUp}
            onPointerCancel={() => {
              setGesture(null)
              setPan(null)
              setIncoming(null)
              setLinkDrop(null)
            }}
            // Losing the pointer without a release ends a pan or an unfinished drag, so nothing stays stuck mid-air.
            onLostPointerCapture={() => {
              setPan(null)
              setGesture((g) => (g && !g.done ? null : g))
              setIncoming(null)
              setLinkDrop(null)
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
                  // A press inside the frame selects it; once it is selected, dragging there moves it too.
                  onPointerDown={(e) =>
                    e.target === e.currentTarget && onItemPointerDown(e, 'bucket', bucket.id, { move: selectedBucket?.id === bucket.id })
                  }
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
                  {!readOnly && selectedBucket?.id === bucket.id && (
                    <ResizeHandles onPointerDown={(e, handle) => onItemPointerDown(e, 'resize', bucket.id, { handle })} />
                  )}
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
                lifted={dragging?.id === image.id && !dragging.done}
                onPointerDown={(e) => onItemPointerDown(e, 'image', image.id)}
                onResizePointerDown={readOnly ? undefined : (e, handle) => onItemPointerDown(e, 'image-resize', image.id, { handle })}
                onDelete={readOnly ? undefined : () => setPendingDelete({ kind: 'image', id: image.id })}
              />
            ))}

            {newPostItSpot && (
              <div
                className="post-it-slot"
                aria-hidden="true"
                style={{ left: newPostItSpot.x, top: newPostItSpot.y, width: NOTE_WIDTH, height: NOTE_HEIGHT }}
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
                lifted={(draggedNote?.note.id === note.id && !dragging?.done) || remoteDrags.some((r) => r.id === note.id && !r.endedAt)}
                dimmed={dimmed(note)}
                dropTarget={imageTarget?.id === note.id || (linkDrop?.window === false && linkDrop.note.id === note.id)}
                onPointerDown={(e) => onItemPointerDown(e, 'note', note.id)}
                onExpand={() => expand(note.id)}
                editingTitle={titleEdit?.id === note.id}
                titleCaret={titleEdit?.id === note.id ? titleEdit.caret : undefined}
                onRenameTitle={readOnly ? undefined : (title) => settle(() => api.updateNote(note.id, { title }))}
                onTitleDone={() => setTitleEdit(null)}
                onEditTags={readOnly ? undefined : (anchor) => setQuickEdit({ noteId: note.id, kind: 'tags', anchor })}
                onEditAssignees={readOnly ? undefined : (anchor) => setQuickEdit({ noteId: note.id, kind: 'people', anchor })}
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
        onPostItPointerDown={onPostItPointerDown}
        onAddImage={() => void addImagesFromPicker()}
        readOnly={readOnly}
      />
      <TagFilter
        boardTags={board.tags}
        value={filter}
        onChange={setTagFilter}
        matching={board.notes.filter((n) => !dimmed(n)).length}
        total={board.notes.length}
      />
      {readOnly && <div className="read-only-badge">View only</div>}
      {hint && <div className="canvas-hint">{hint}</div>}
      <ViewControls
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={extent}
        viewport={{ x: (view.x - origin.x) / zoom, y: (view.y - origin.y) / zoom, width: view.width / zoom, height: view.height / zoom }}
        buckets={board.buckets}
        notes={board.notes}
        dimmed={dimmed}
        images={images.map((i) => i.image)}
        onNavigate={(center) => {
          const el = scrollRef.current!
          el.scrollLeft = center.x * zoom + origin.x - el.clientWidth / 2
          el.scrollTop = center.y * zoom + origin.y - el.clientHeight / 2
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

      {quickNote && quickEdit?.kind === 'tags' && (
        <TagPicker
          key={quickNote.id}
          anchor={quickEdit.anchor}
          tags={quickNote.tags}
          boardTags={board.tags}
          onChange={(tags) => settle(() => api.updateNote(quickNote.id, { tags }))}
          onRename={renameTag}
          onDelete={deleteTag}
          onClose={() => setQuickEdit(null)}
        />
      )}
      {quickNote && quickEdit?.kind === 'people' && (
        <AssigneePicker
          key={quickNote.id}
          anchor={quickEdit.anchor}
          assignees={quickNote.assignees}
          people={board.people}
          onChange={(assigneeIds) => settle(() => api.updateNote(quickNote.id, { assigneeIds }))}
          onClose={() => setQuickEdit(null)}
        />
      )}

      {expandedNote && (
        <ExpandedNote
          key={expandedNote.id}
          note={expandedNote}
          bucket={board.buckets.find((b) => b.id === expandedNote.bucketId)}
          images={board.images.filter((i) => i.noteId === expandedNote.id)}
          notes={board.notes}
          handle={expandedRef}
          run={run}
          onBusy={setNoteBusy}
          incoming={incoming}
          linking={!!linkDrop?.window}
          boardPoint={boardPoint}
          boardZoom={zoom}
          focusTitle={expanded?.fresh ?? false}
          onChange={(changes) => settle(() => api.updateNote(expandedNote.id, changes))}
          boardTags={board.tags}
          people={board.people}
          onRenameTag={renameTag}
          onDeleteTag={deleteTag}
          onDelete={() => deleteNote(expandedNote.id)}
          onAddImages={(files, at) => void addImages(files, { noteId: expandedNote.id, at })}
          onPickImages={() => void addImagesFromPicker({ noteId: expandedNote.id })}
          onClose={() => setExpanded(null)}
          readOnly={readOnly}
        />
      )}

      {/* The board draws below the open post-it, so a post-it dragged over its window is shown on top, at the pointer. */}
      {draggedNote &&
        dragging &&
        linkDrop?.window &&
        createPortal(
          <div
            className="drag-ghost post-it-ghost"
            aria-hidden="true"
            style={{
              left: linkDrop.client.x - (dragging.start.x - draggedNote.note.x) * zoom,
              top: linkDrop.client.y - (dragging.start.y - draggedNote.note.y) * zoom,
              transform: `scale(${zoom})`,
            }}
          >
            <PostIt note={draggedNote.note} point={{ x: 0, y: 0 }} selected={false} lifted onPointerDown={() => {}} onExpand={() => {}} />
          </div>,
          document.body,
        )}

      <Toast message={toast} onDone={clearToast} />

      {/* A new post-it carried from the tool rail follows the pointer, at the board's zoom. */}
      {newPostIt &&
        createPortal(
          <div
            className="drag-ghost post-it-ghost"
            aria-hidden="true"
            style={{
              left: newPostIt.x - (NOTE_WIDTH / 2) * zoom,
              top: newPostIt.y - (NOTE_HEIGHT / 2) * zoom,
              transform: `scale(${zoom})`,
            }}
          >
            <div className={`post-it lifted fill-${fill.toLowerCase()}`} style={{ width: NOTE_WIDTH, height: NOTE_HEIGHT }}>
              <div className="post-it-head">
                <span className="post-it-title">New post-it</span>
              </div>
            </div>
          </div>,
          document.body,
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
