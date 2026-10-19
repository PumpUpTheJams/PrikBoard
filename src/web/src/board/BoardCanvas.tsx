import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../api/client'
import type {
  BoardDetail,
  BoardTag,
  BucketDetail,
  DragKind,
  DragUpdate,
  ImageDetail,
  NoteChanges,
  NoteColor,
  NoteDetail,
  Point,
  Rect,
  ResizeHandle,
  TagColor,
} from '../api/types'
import { presenceColor } from '../live/presence'
import type { LiveDrag } from '../live/useLive'
import { Toast, type ToastMessage } from '../components/Toast'
import { AssigneePicker } from './AssigneePicker'
import { BoardImageView } from './BoardImageView'
import { BoardSearch, type SearchResult } from './BoardSearch'
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
import { matchNote, searchTerms, type SearchField } from './search'
import { tagColorsOf, type TagColorPreview } from './tagColors'
import { TagFilter } from './TagFilter'
import { TagPicker } from './TagPicker'
import { ToolRail } from './ToolRail'
import { toolKeys, type Tool } from './tools'
import type { IncomingImage } from './NoteCanvas'
import type { BoardState } from './useBoard'
import { created, deleted, noteEdit, patched, snap, step, target, type RecordingRun, type UndoEntry } from './undo'
import { useUndo, type UndoHistory } from './useUndo'
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
/** Room above the board while searching, so a post-it at the top can be scrolled below the list of results. */
const SEARCH_MARGIN = 480
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
  const title = node?.parentElement?.closest('.post-it-title')
  if (!node || offset === undefined || !title) return undefined
  // Search marks split the title into several pieces of text, so count from the start of the title.
  const range = document.createRange()
  range.setStart(title, 0)
  range.setEnd(node, offset)
  return range.toString().length
}

/** A rectangle in the board's viewport, in screen pixels. */
interface Box {
  left: number
  top: number
  right: number
  bottom: number
}

interface BoardCanvasProps {
  board: BoardDetail
  run: BoardState['run']
  /** Where this board's undo steps are kept (they outlive the view). */
  undoHistory: UndoHistory
  setPaused: BoardState['setPaused']
  /** Other people's drags in progress (live updates). */
  remoteDrags?: LiveDrag[]
  /** Your drag in progress, for the others to see; null when it ends. */
  onDrag?: (update: DragUpdate | null) => void
}

export function BoardCanvas({ board, run, undoHistory, setPaused, remoteDrags = [], onDrag }: BoardCanvasProps) {
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
  /** The tag or people window opened from a post-it on the board, next to the part that was clicked (screen pixels). */
  const [quickEdit, setQuickEdit] = useState<{ noteId: string; kind: 'tags' | 'people'; anchor: DOMRect } | null>(null)
  /** Tags to filter on: post-its with none of them fade. Not kept: every board starts unfiltered. */
  const [tagFilter, setTagFilter] = useState<string[]>([])
  /** A tag colour under the pointer in the palette, and one being saved: both show on the tag everywhere. */
  const [tagPreview, setTagPreview] = useState<TagColorPreview | null>(null)
  const [tagSaving, setTagSaving] = useState<TagColorPreview | null>(null)
  /**
   * The search (BoardSearch): closed (null), or its words, the result picked (null: the first), and whether its list of
   * results shows. Not kept either.
   */
  const [search, setSearch] = useState<{ query: string; current: string | null; listOpen: boolean } | null>(null)
  const searchInput = useRef<HTMLInputElement>(null)
  /** Focus for the search field after the next render: with its words selected, or the text cursor at the end. */
  const focusSearch = useRef<'select' | 'end' | null>(null)
  /** Scroll the result being looked at into view after the next render, once the list of results has its new size. */
  const revealPending = useRef(false)
  /** Where a smooth scroll to a result is heading: quick key repeats are measured from there, not from a frame halfway. */
  const revealTarget = useRef<{ left: number; top: number; until: number } | null>(null)
  /** The search, hints and view-only badge at the top of the board. */
  const boardTop = useRef<HTMLDivElement>(null)
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
   * board can still be scrolled so any part of it, its top-left corner too, sits in the middle of the screen. While
   * searching there is more room above, so a post-it at the top can be scrolled below the list of results.
   */
  const margin = { x: Math.round(view.width / 2), y: Math.round(Math.max(view.height / 2, search ? SEARCH_MARGIN : 0)) }
  /** Where the canvas's top-left corner is in the scrolled area: the margin, and the tool rail's room on the left. */
  const origin = { x: margin.x + RAIL_INSET, y: margin.y }
  // The wheel zooms the board (Shift + wheel scrolls). A quick-edit window sits next to the post-it on screen; after
  // zooming it would point at nothing.
  const { zoom, zoomTo } = useZoom(scrollRef, origin, { onZoom: () => setQuickEdit(null), wheel: 'always' })
  /** A short confirmation at the bottom of the board (a link was added, an image went into a post-it, an undo). */
  const [toast, setToast] = useState<ToastMessage | null>(null)
  const clearToast = useCallback(() => setToast(null), [])
  const notify = (message: string | Omit<ToastMessage, 'id'>) =>
    setToast({ id: Date.now(), ...(typeof message === 'string' ? { text: message } : message) })
  const undo = useUndo({
    boardId: board.id,
    history: undoHistory,
    run,
    notify,
    moving: (id) => remoteDrags.some((r) => r.id === id && !r.endedAt),
    onApplied: (entry, direction) => {
      // A post-it or item that is gone now closes and is no longer selected.
      for (const { target: t, before, after } of entry.changes) {
        if ((direction === 'undo' ? before : after) !== null) continue
        if (t.kind === 'note' && expanded?.id === t.id) setExpanded(null)
        if (selection?.id === t.id) setSelection(null)
      }
    },
  })
  /** Runs a change through the board and records it for undo, as `describe` tells from its result. */
  const runRec: RecordingRun = (action, describe) =>
    undo.track(
      run(async () => {
        const result = await action()
        const entry = describe?.(result)
        if (entry) undo.record(entry)
      }),
    )
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
      if (e.target instanceof Element && e.target.closest('input, textarea, select, [role="menu"], [role="dialog"], [role="alertdialog"]')) return
      if (e.key === 'Escape') {
        if (expanded) return
        setTool('select')
        setSelection(null)
      } else if (readOnly) {
        if (e.key.toLowerCase() === 'v') setTool('select')
      } else if ((e.key === 'Delete' || e.key === 'Backspace') && (selection?.kind === 'note' || selection?.kind === 'image')) {
        // No question first: the toast that follows has an Undo button, and Ctrl/Cmd+Z works too.
        e.preventDefault()
        if (!e.repeat) (selection.kind === 'note' ? deleteNote : deleteImage)(selection.id)
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

  // "/" or Ctrl/Cmd+F opens the search, or selects its words. Fields, menus and other windows keep those keys; the open
  // post-it gives way to the search.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      const find = (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'f'
      const slash = e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey
      if (!find && !slash) return
      const target = e.target instanceof Element ? e.target : null
      if (target && target === searchInput.current) {
        if (find) {
          e.preventDefault()
          searchInput.current.select()
        }
        return
      }
      if (target?.closest('input, textarea, select, [role="menu"], [role="alertdialog"]')) return
      if (target?.closest('[role="dialog"]') && !target.closest('.expanded')) return
      e.preventDefault()
      openSearch()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  // Ctrl/Cmd+Z undoes, Ctrl/Cmd+Shift+Z or Ctrl+Y redoes: on the board and in the open post-it. In a field the browser
  // undoes typing instead, and other windows keep the keys.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (!(e.ctrlKey || e.metaKey) || e.altKey) return
      const key = e.key.toLowerCase()
      const redo = (key === 'z' && e.shiftKey) || (key === 'y' && !e.shiftKey)
      if (key !== 'z' && !redo) return
      const target = e.target instanceof Element ? e.target : null
      if (target?.closest('input, textarea, select, [contenteditable="true"], [role="menu"], [role="alertdialog"]')) return
      if (target?.closest('[role="dialog"]') && !target.closest('.expanded')) return
      e.preventDefault()
      if (readOnly || gesture || noteBusy) return
      if (redo) undo.redo()
      else undo.undo()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  useLayoutEffect(() => {
    const input = searchInput.current
    if (!focusSearch.current || !input) return
    input.focus({ preventScroll: true })
    if (focusSearch.current === 'select') input.select()
    else input.setSelectionRange(input.value.length, input.value.length)
    focusSearch.current = null
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

  /** Runs a change through the board (recorded for undo as `describe` says), then lets go of the finished gesture. */
  async function settle<T>(action: () => Promise<T>, describe?: (result: T) => UndoEntry | null | undefined) {
    const ok = await runRec(action, describe)
    setGesture(null)
    return ok
  }

  /** Edits a post-it's fields, as one undo step. */
  const editNote = (note: NoteDetail, changes: NoteChanges) =>
    settle(() => api.updateNote(note.id, changes), (after) => noteEdit(note, after, changes))

  /** Deletes a post-it, closing it if it was open. */
  function deleteNote(id: string) {
    const note = board.notes.find((n) => n.id === id)
    if (!note) return
    if (expanded?.id === id) setExpanded(null)
    setSelection(null)
    void settle(
      () => api.deleteNote(id),
      () => step('Delete post-it', [deleted(target.note(id), snap.note(note))], { toast: 'Post-it deleted' }),
    )
  }

  /** Copies a post-it's content one grid step down and right of it, and selects the copy. Attached images stay behind. */
  function duplicateNote(id: string) {
    const note = board.notes.find((n) => n.id === id)
    if (!note) return
    void settle(
      async () => {
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
        return copy
      },
      (copy) => step('Duplicate post-it', [created(target.note(copy.id), snap.note(copy))]),
    )
  }

  const renameTag = (tag: BoardTag, name: string) =>
    settle(
      () => api.updateBoardTag(board.id, tag.id, { name }),
      (renamed) => step('Rename tag', [patched(target.tag(tag.id), snap.tag(tag), snap.tag(renamed), ['name'])]),
    )
  /** Recolours a tag; the new colour shows right away and stays until the reloaded board has it. */
  async function recolorTag(tag: BoardTag, color: TagColor) {
    setTagSaving({ tagId: tag.id, color })
    const ok = await settle(
      () => api.updateBoardTag(board.id, tag.id, { color }),
      (recolored) => step('Change tag colour', [patched(target.tag(tag.id), snap.tag(tag), snap.tag(recolored), ['color'])]),
    )
    setTagSaving((s) => (s?.tagId === tag.id && s.color === color ? null : s))
    return ok
  }
  const deleteTag = (tag: BoardTag) =>
    settle(
      () => api.deleteBoardTag(board.id, tag.id),
      () => step('Delete tag', [deleted(target.tag(tag.id), snap.tag(tag))], { toast: `Tag “${tag.name}” deleted` }),
    )

  function deleteImage(id: string) {
    const image = board.images.find((i) => i.id === id)
    if (!image) return
    if (selection?.id === id) setSelection(null)
    void settle(
      () => api.deleteImage(id),
      () => step('Delete image', [deleted(target.image(id), snap.image(image))], { toast: 'Image deleted' }),
    )
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
  async function addImages(files: File[], place: { at?: Point; noteId?: string }) {
    if (files.length === 0) return
    const at = place.at ?? viewportCenter()
    setUploads((n) => n + 1)
    await undo.track(
      run(async () => {
        const added: ImageDetail[] = []
        try {
          for (const [i, file] of files.entries()) {
            const placement = place.noteId
              ? { noteId: place.noteId, ...(place.at ? snapPoint({ x: place.at.x + i * 40, y: place.at.y + i * 40 }) : {}) }
              : snapPoint({ x: at.x - IMAGE_OFFSET.x + i * 40, y: at.y - IMAGE_OFFSET.y + i * 40 })
            added.push(await api.uploadImage(board.id, file, placement))
          }
        } finally {
          // One step for all of them; when one fails, the ones that made it.
          const entry = step(added.length === 1 ? 'Add image' : 'Add images', added.map((image) => created(target.image(image.id), snap.image(image))))
          if (entry) undo.record(entry)
        }
        if (added.length === 1 && !place.noteId) setSelection({ kind: 'image', id: added[0].id })
      }),
    )
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
   * links there; otherwise the undo step.
   */
  async function linkNote({ note, at }: LinkDrop, linked: NoteDetail): Promise<UndoEntry | null | false> {
    if (note.blocks.some((b) => b.links?.some((l) => noteRoute(l.url)?.noteId === linked.id))) return false
    const link = { title: linked.title, url: linked.path }
    const lists = note.blocks.filter((b) => b.kind === 'Links')
    const block =
      (at && lists.find((b) => at.x >= b.x && at.x < b.x + b.width && at.y >= b.y && at.y < b.y + b.height)) ?? lists[0]
    if (block) {
      const updated = await api.updateNoteBlock(note.id, block.id, { links: [...(block.links ?? []), link] })
      return step('Link post-it', [patched(target.block(note.id, block.id), snap.block(block), snap.block(updated), ['links'])])
    }
    const place = at ? onNoteCanvas({ x: at.x - GRID, y: at.y - GRID }, defaultBlockSize('Links')) : {}
    const added = await api.addNoteBlock(note.id, { kind: 'Links', links: [link], ...place })
    return step('Link post-it', [created(target.block(note.id, added.id), snap.block(added))])
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

  /** Closes the open post-it; while searching, back to the results. */
  function closeExpanded() {
    setExpanded(null)
    if (!search) return
    setSearch({ ...search, listOpen: true })
    focusSearch.current = 'end'
  }

  /** Opens the search with its words selected. An open post-it closes: nothing in it was being typed into. */
  function openSearch() {
    setExpanded(null)
    setSearch((s) => ({ query: s?.query ?? '', current: s?.current ?? null, listOpen: true }))
    focusSearch.current = 'select'
  }

  function closeSearch() {
    setSearch(null)
    revealTarget.current = null
  }

  function changeQuery(query: string) {
    setSearch((s) => s && { ...s, query, listOpen: true })
    revealPending.current = true
  }

  /** Moves through the results, wrapping around; the board scrolls to the new one. */
  function moveInResults(direction: 1 | -1) {
    if (!search) return
    const i = results.findIndex((r) => r.note.id === current)
    const next = results.length > 0 ? results[(i + direction + results.length) % results.length].note.id : null
    setSearch({ ...search, current: next, listOpen: true })
    revealPending.current = true
  }

  /** Opens a result as a post-it opens anywhere. The search keeps its words, so Escape comes back to the same results. */
  function openResult(id: string) {
    setSearch((s) => s && { ...s, current: id, listOpen: false })
    // Out of the field, so Escape and typing go to the post-it, not to the search behind it.
    searchInput.current?.blur()
    expand(id)
  }

  /**
   * Scrolls the board so a post-it is on screen and not under the search, its results or another floating control. When
   * it has to move, it lands in the middle of the space below the search.
   */
  function reveal(id: string) {
    const el = scrollRef.current
    const note = board.notes.find((n) => n.id === id)
    if (!el || !note) return
    const frame = el.getBoundingClientRect()
    const target = revealTarget.current && revealTarget.current.until > performance.now() ? revealTarget.current : null
    const from = target ?? { left: el.scrollLeft, top: el.scrollTop }
    const at = notePoint(note)
    const left = at.x * zoom + origin.x - from.left
    const top = at.y * zoom + origin.y - from.top
    const card: Box = { left, top, right: left + NOTE_WIDTH * zoom, bottom: top + NOTE_HEIGHT * zoom }
    // Floating controls, with a little room around them.
    const around = (node: Element | null | undefined): Box | null => {
      const r = node?.getBoundingClientRect()
      if (!r || r.width === 0) return null
      return { left: r.left - frame.left - 8, top: r.top - frame.top - 8, right: r.right - frame.left + 8, bottom: r.bottom - frame.top + 8 }
    }
    const section = el.parentElement
    const stack = around(boardTop.current)
    const rail = around(section?.querySelector('.tool-rail'))
    const covers = [stack, rail, around(section?.querySelector('.view-controls')), around(section?.querySelector('.tag-filter'))]
    const covered = covers.some((r) => r && card.left < r.right && card.right > r.left && card.top < r.bottom && card.bottom > r.top)
    if (!covered && card.left >= 0 && card.top >= 0 && card.right <= el.clientWidth && card.bottom <= el.clientHeight) return
    const area: Box = { left: rail?.right ?? 16, top: stack?.bottom ?? 16, right: el.clientWidth - 16, bottom: el.clientHeight - 16 }
    const x = (area.left + area.right) / 2
    const y = Math.max(area.top + (NOTE_HEIGHT * zoom) / 2, (area.top + area.bottom) / 2)
    const to = {
      left: Math.min(Math.max(0, (at.x + NOTE_WIDTH / 2) * zoom + origin.x - x), el.scrollWidth - el.clientWidth),
      top: Math.min(Math.max(0, (at.y + NOTE_HEIGHT / 2) * zoom + origin.y - y), el.scrollHeight - el.clientHeight),
    }
    revealTarget.current = { ...to, until: performance.now() + 600 }
    el.scrollTo({ ...to, behavior: reducedMotion() ? 'auto' : 'smooth' })
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
    // A press on a search result makes it the one being looked at.
    if (kind === 'note' && search && results.some((r) => r.note.id === id)) setSearch({ ...search, current: id, listOpen: false })
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
    void settle(
      async () => {
        const note = await api.createNote(board.id, { title: 'New post-it', color: fill, ...postItSpot(p) })
        setSelection({ kind: 'note', id: note.id })
        setExpanded({ id: note.id, fresh: true })
        setTool('select')
        return note
      },
      (note) => step('Add post-it', [created(target.note(note.id), snap.note(note))]),
    )
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
      void settle(
        async () => {
          const bucket = await api.createBucket(board.id, { name: 'New bucket', ...rect })
          setSelection({ kind: 'bucket', id: bucket.id, fresh: true })
          setTool('select')
          return bucket
        },
        (bucket) => step('Add bucket', [created(target.bucket(bucket.id), snap.bucket(bucket))]),
      )
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
      let added = false
      void runRec(
        () => linkNote(link, linked),
        (entry) => {
          added = entry !== false
          return entry || null
        },
      ).then((ok) => {
        if (!ok) return
        if (!added) return notify('Already linked')
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
    /** The undo step for an image that moved, was resized or went into a post-it. */
    const imageStep = (label: string, before: ImageDetail | undefined, keys: string[]) => (after: ImageDetail) =>
      before ? step(label, [patched(target.image(id), snap.image(before), snap.image(after), keys)]) : null
    if (expanded && onNote) {
      // Dropped on the open post-it: the image moves onto its canvas.
      setSelection(null)
      void settle(
        () => api.updateImage(id, { noteId: expanded.id, ...onNoteCanvas(onNote.at, onNote.image) }),
        imageStep('Embed image', onNote.image, ['noteId', 'x', 'y']),
      ).then((ok) => ok && notify('Image embedded'))
    } else if (intoNote) {
      // Dropped on a post-it on the board: sucked into it and attached, in a free spot on its canvas, without opening it.
      // The request waits for the animation, so the reload doesn't take the image away halfway.
      setSelection(null)
      const animation = suckInto(id, intoNote)
      void settle(
        async () => {
          await animation?.finished.catch(() => {})
          return api.updateImage(id, { noteId: intoNote.id })
        },
        imageStep('Embed image', board.images.find((i) => i.id === id), ['noteId', 'x', 'y']),
      ).then((ok) => (ok ? notify('Image embedded') : animation?.cancel()))
    } else if (gesture.kind === 'note') {
      const note = board.notes.find((n) => n.id === id)!
      void settle(
        () => api.moveNote(id, notePoint(note)),
        (moved) => step('Move post-it', [patched(target.note(id), snap.note(note), snap.note(moved), ['x', 'y'])]),
      )
    } else if (gesture.kind === 'image' || gesture.kind === 'image-resize') {
      const image = board.images.find((i) => i.id === id)!
      const { x, y, width } = imageRect(image)
      const moving = gesture.kind === 'image'
      void settle(
        () => api.updateImage(id, moving ? { x, y } : { x, y, width }),
        imageStep(moving ? 'Move image' : 'Resize image', image, ['x', 'y', 'width']),
      )
    } else if (gesture.kind === 'bucket') {
      // A moved bucket carries its post-its along; undoing the move takes exactly those back.
      const bucket = board.buckets.find((b) => b.id === id)!
      const carried = board.notes.filter((n) => n.bucketId === id)
      const { x, y } = bucketRect(bucket)
      void settle(
        () => api.updateBucket(id, { x, y }),
        (moved) => {
          const d = { x: moved.x - bucket.x, y: moved.y - bucket.y }
          return step('Move bucket', [
            patched(target.bucket(id, carried.map((n) => n.id)), snap.bucket(bucket), snap.bucket(moved), ['x', 'y']),
            ...carried.map((n) =>
              patched(target.note(n.id), { x: n.x, y: n.y }, { x: Math.max(0, n.x + d.x), y: Math.max(0, n.y + d.y) }, ['x', 'y'], true),
            ),
          ])
        },
      )
    } else {
      // With a new size, a new place is a resize from a top or left corner or edge: the server leaves the post-its where they are.
      const bucket = board.buckets.find((b) => b.id === id)!
      const { x, y, width, height } = bucketRect(bucket)
      void settle(
        () => api.updateBucket(id, { x, y, width, height }),
        (resized) => step('Resize bucket', [patched(target.bucket(id), snap.bucket(bucket), snap.bucket(resized), ['x', 'y', 'width', 'height'])]),
      )
    }
  }

  const buckets = board.buckets.map((bucket) => ({ bucket, rect: bucketRect(bucket) }))
  const images = board.images.filter((i) => i.noteId === null).map((image) => ({ image, rect: imageRect(image) }))
  /** How each tag shows: its saved colour, or one being saved or previewed. */
  const tagColors = tagColorsOf(board.tags, tagSaving, tagPreview)
  /** How many images are attached to each post-it. */
  const attached = new Map<string, number>()
  for (const image of board.images) if (image.noteId) attached.set(image.noteId, (attached.get(image.noteId) ?? 0) + 1)
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
  const countIn = (bucketId: string) => board.notes.filter((n) => n.bucketId === bucketId).length
  // Filter on tags that still exist (one may have been renamed or deleted meanwhile); any of them matches.
  const filter = tagFilter.filter((t) => board.tags.some((bt) => bt.name === t))
  const tagFaded = (note: NoteDetail) => filter.length > 0 && !note.tags.some((t) => filter.includes(t))
  // The search fades the post-its without its words too. Its results are the matches the tag filter lets through, in
  // board order (bucket by bucket, loose post-its last).
  const terms = useMemo(() => searchTerms(search?.query ?? ''), [search?.query])
  const matches = useMemo(() => {
    const found = new Map<string, SearchField[]>()
    if (terms.length === 0) return found
    for (const note of board.notes) {
      const fields = matchNote(note, terms)
      if (fields) found.set(note.id, fields)
    }
    return found
  }, [board.notes, terms])
  const dimmed = (note: NoteDetail) => tagFaded(note) || (terms.length > 0 && !matches.has(note.id))
  const results: SearchResult[] = board.notes
    .filter((n) => matches.has(n.id) && !tagFaded(n))
    .map((note) => ({ note, fields: matches.get(note.id)! }))
  /** The result being looked at: the one picked, or else the first. */
  const current = results.find((r) => r.note.id === search?.current)?.note.id ?? results[0]?.note.id ?? null

  useLayoutEffect(() => {
    if (!revealPending.current) return
    revealPending.current = false
    if (current) reveal(current)
  })
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
                onDelete={readOnly ? undefined : () => deleteImage(image.id)}
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
                images={attached.get(note.id)}
                tagColors={tagColors}
                selected={selection?.kind === 'note' && selection.id === note.id}
                lifted={(draggedNote?.note.id === note.id && !dragging?.done) || remoteDrags.some((r) => r.id === note.id && !r.endedAt)}
                dimmed={dimmed(note)}
                highlight={matches.has(note.id) ? terms : undefined}
                current={note.id === current}
                dropTarget={imageTarget?.id === note.id || (linkDrop?.window === false && linkDrop.note.id === note.id)}
                onPointerDown={(e) => onItemPointerDown(e, 'note', note.id)}
                onExpand={() => expand(note.id)}
                editingTitle={titleEdit?.id === note.id}
                titleCaret={titleEdit?.id === note.id ? titleEdit.caret : undefined}
                onRenameTitle={readOnly ? undefined : (title) => editNote(note, { title })}
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
        searching={!!search}
        onSearch={() => (search ? closeSearch() : openSearch())}
      />
      <TagFilter
        boardTags={board.tags}
        tagColors={tagColors}
        value={filter}
        onChange={setTagFilter}
        matching={board.notes.filter((n) => !tagFaded(n)).length}
        total={board.notes.length}
      />
      <div className="board-top" ref={boardTop}>
        {search && (
          <BoardSearch
            inputRef={searchInput}
            query={search.query}
            onQueryChange={changeQuery}
            terms={terms}
            results={results}
            current={current}
            listOpen={search.listOpen && !expandedNote}
            onListOpenChange={(listOpen) => setSearch((s) => s && { ...s, listOpen })}
            onMove={moveInResults}
            onOpen={openResult}
            onClose={closeSearch}
            faded={board.notes.filter((n) => matches.has(n.id) && tagFaded(n)).length}
            onClearFilter={() => {
              setTagFilter([])
              revealPending.current = true
            }}
            bucketName={(note) => board.buckets.find((b) => b.id === note.bucketId)?.name ?? 'Loose'}
          />
        )}
        {readOnly && <div className="read-only-badge">View only</div>}
        {hint && <div className="canvas-hint">{hint}</div>}
      </div>
      <ViewControls
        undo={readOnly ? undefined : { label: undo.nextUndo, disabled: !!gesture || noteBusy, onClick: undo.undo }}
        redo={readOnly ? undefined : { label: undo.nextRedo, disabled: !!gesture || noteBusy, onClick: undo.redo }}
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={extent}
        viewport={{ x: (view.x - origin.x) / zoom, y: (view.y - origin.y) / zoom, width: view.width / zoom, height: view.height / zoom }}
        buckets={board.buckets}
        notes={board.notes}
        dimmed={dimmed}
        current={current}
        images={images.map((i) => i.image)}
        onNavigate={(center) => {
          const el = scrollRef.current!
          el.scrollLeft = center.x * zoom + origin.x - el.clientWidth / 2
          el.scrollTop = center.y * zoom + origin.y - el.clientHeight / 2
        }}
      />

      {selectedBucket && (
        <BucketInspector
          // A new name (an undo, someone else) starts the field afresh.
          key={`${selectedBucket.id}:${selectedBucket.name}`}
          bucket={selectedBucket}
          noteCount={countIn(selectedBucket.id)}
          autoFocus={!!selection?.fresh}
          onSave={(changes) =>
            settle(
              () => api.updateBucket(selectedBucket.id, changes),
              (after) => step('Rename bucket', [patched(target.bucket(after.id), snap.bucket(selectedBucket), snap.bucket(after), ['name'])]),
            )
          }
          onDelete={() => {
            setSelection(null)
            void settle(
              () => api.deleteBucket(selectedBucket.id),
              () => step('Delete bucket', [deleted(target.bucket(selectedBucket.id), snap.bucket(selectedBucket))], { toast: 'Bucket deleted' }),
            )
          }}
          onClose={() => setSelection(null)}
        />
      )}

      {selectedImage && (
        <ImageInspector
          key={`${selectedImage.id}:${selectedImage.caption}`}
          image={selectedImage}
          onSave={(changes) =>
            settle(
              () => api.updateImage(selectedImage.id, changes),
              (after) => step('Edit caption', [patched(target.image(after.id), snap.image(selectedImage), snap.image(after), ['caption'])]),
            )
          }
          onDelete={() => deleteImage(selectedImage.id)}
          onClose={() => setSelection(null)}
        />
      )}

      {quickNote && quickEdit?.kind === 'tags' && (
        <TagPicker
          key={quickNote.id}
          anchor={quickEdit.anchor}
          tags={quickNote.tags}
          boardTags={board.tags}
          tagColors={tagColors}
          onChange={(tags) => editNote(quickNote, { tags })}
          onRename={renameTag}
          onRecolor={recolorTag}
          onPreview={setTagPreview}
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
          onChange={(assigneeIds) => editNote(quickNote, { assigneeIds })}
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
          run={runRec}
          onBusy={setNoteBusy}
          incoming={incoming}
          linking={!!linkDrop?.window}
          boardPoint={boardPoint}
          boardZoom={zoom}
          focusTitle={expanded?.fresh ?? false}
          onChange={(changes) => editNote(expandedNote, changes)}
          boardTags={board.tags}
          tagColors={tagColors}
          people={board.people}
          onRenameTag={renameTag}
          onRecolorTag={recolorTag}
          onPreviewTagColor={setTagPreview}
          onDeleteTag={deleteTag}
          onDelete={() => deleteNote(expandedNote.id)}
          onAddImages={(files, at) => void addImages(files, { noteId: expandedNote.id, at })}
          onPickImages={() => void addImagesFromPicker({ noteId: expandedNote.id })}
          onClose={closeExpanded}
          readOnly={readOnly}
          highlight={matches.has(expandedNote.id) ? terms : undefined}
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
            <PostIt note={draggedNote.note} point={{ x: 0, y: 0 }} images={attached.get(draggedNote.note.id)} tagColors={tagColors} selected={false} lifted onPointerDown={() => {}} onExpand={() => {}} />
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
    </section>
  )
}
