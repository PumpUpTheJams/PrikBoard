import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent } from 'react'
import { createPortal } from 'react-dom'
import { api, errorMessage } from '../api/client'
import { cardColors } from '../api/types'
import type {
  BoardClip,
  BoardDetail,
  BoardTag,
  BucketDetail,
  DragKind,
  DragUpdate,
  DrawingDetail,
  ImageDetail,
  InkColor,
  CardChanges,
  CardColor,
  CardDetail,
  GrowSide,
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
import { ExpandedCard, type ExpandedCardHandle } from './ExpandedCard'
import { BOARD_PEN, inkHex, movePenRing, usePen } from './drawings'
import { DrawingToolbar, DrawingView, PenRing, StrokeSvg } from './DrawingView'
import { TrashWindow, type OutgoingCard, type TrashWindowHandle } from './TrashWindow'
import { clipSummary, readClip, writeClip } from './clipboard'
import { draggingFiles, imageFiles, pickImageFiles } from './files'
import { delta, isDrag } from './gesture'
import { boardRefFromHash, isBoard, linksTo, cardInHash, cardLabel, trashInHash, trashPath } from './links'
import { CardIcon, ChevronDownIcon, ChevronUpIcon, ImageIcon } from './icons'
import { pop, reducedMotion } from './motion'
import { BucketInspector, ImageInspector } from './Inspector'
import {
  GRID,
  MIN_BUCKET_HEIGHT,
  MIN_BUCKET_WIDTH,
  MIN_COORDINATE,
  CARD_HEIGHT,
  CARD_WIDTH,
  bucketSize,
  backToFront,
  bucketUnderCard,
  collapsedRect,
  defaultComponentSize,
  onBoard,
  packIn,
  onCardCanvas,
  resizeFrom,
  resizeImage,
  snapPoint,
  spanRect,
} from './layout'
import { Card } from './Card'
import { resizeCursor } from './resizeCursor'
import { ResizeHandles } from './ResizeHandles'
import { ops } from './ops'
import { matchCard, searchTerms, type SearchField } from './search'
import { SelectionToolbar, type CardSort } from './SelectionToolbar'
import { adds, contains, isPicked, mergePicked, togglePicked, touches, useSpaceHeld } from './selection'
import { shakeDetector } from './shake'
import { tagColorsOf, type TagColorPreview } from './tagColors'
import { TagFilter } from './TagFilter'
import { TagPicker } from './TagPicker'
import { ToolRail } from './ToolRail'
import { toolKeys, type Tool } from './tools'
import type { IncomingDrawing, IncomingImage } from './CardCanvas'
import type { BoardState } from './useBoard'
import { created, deleted, patched, snap, step, target, type RecordingRun, type UndoChange, type UndoEntry } from './undo'
import { useStroke } from './useStroke'
import { useUndo, type UndoHistory } from './useUndo'
import { useZoom } from './useZoom'
import { ViewControls } from './ViewControls'
import { stepZoom } from './zoom'

// Loaded on first open: the Markdown renderer is the biggest part of it.

type SelectionKind = 'card' | 'bucket' | 'image' | 'drawing'

interface Selection {
  kind: SelectionKind
  id: string
  /** Just created: its name or title is focused so it can be typed straight away. */
  fresh?: boolean
}

/**
 * A pointer gesture on the canvas, in canvas coordinates. A finished gesture (`done`) stays until the board has
 * reloaded, so the dropped item doesn't jump back to its old place while the request is in flight. Dragging a card or
 * a group, `home` is the area the items took up when it began, and `away` says the pointer has left it since: released
 * back over it, the drag is called off.
 */
type Gesture = GestureTarget & { start: Point; current: Point; done?: boolean; home?: Rect; away?: boolean }
type ItemGesture = 'card' | 'bucket' | 'resize' | 'image' | 'image-resize' | 'drawing'
/**
 * `handle`: for resizes, the corner or edge being dragged. `group`: moving several selected items at once, `held` the
 * one pressed and `members` the ones that move themselves (not those in a bucket that moves too: it carries them);
 * cards only, shaken, they line up in a grid (`grid`: where each goes, before the drag's distance is added).
 * `select`: the selection rectangle, over the board or inside one bucket (`bucketId`), adding to `base`.
 */
type GestureTarget =
  | { kind: ItemGesture; id: string; handle?: ResizeHandle }
  | { kind: 'draw' }
  | { kind: 'group'; id: string; held: SelectionKind; members: Selection[]; grid?: Record<string, Point> }
  | { kind: 'select'; bucketId: string | null; base: Selection[] }

/**
 * Where a dragged card would be linked on release: the open card's window (`window`; `at` is the point on its
 * canvas, when over the canvas) or another card on the board.
 */
interface LinkDrop {
  card: CardDetail
  window: boolean
  at?: Point
  /** The pointer, in screen pixels. */
  client: Point
}

/**
 * Where several dragged images and drawings would be attached on release: the open card's window (`window`) or a card
 * on the board.
 */
interface AttachDrop {
  card: CardDetail
  window: boolean
}

/**
 * Several selected cards dragged over a bucket they aren't all in: on release they go into its free spots, and it grows
 * towards `side` (the edge nearest the pointer) when they don't fit.
 */
interface ArrangeDrop {
  bucket: BucketDetail
  side: GrowSide
  /** Where each of the group's cards goes. */
  spots: Record<string, Point>
  /** The bucket as it would be after growing. */
  grown: Rect
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
/** Room above the board while searching, so a card at the top can be scrolled below the list of results. */
const SEARCH_MARGIN = 480
/** Where a new image's top-left goes relative to the point it was dropped, pasted or added at. */
const IMAGE_OFFSET = { x: 180, y: 120 }

const hints: Partial<Record<Tool, string>> = {
  card: 'Click anywhere to stick a card',
  bucket: 'Drag across the board to draw a bucket',
  pen: 'Draw anywhere on the board. Esc or V to stop',
}

/** A drag applied on top of the server's layout: your own gesture, or someone else's relayed by live updates. */
interface ActiveDrag {
  kind: ItemGesture
  id: string
  d: Point
  handle: ResizeHandle
}

const fromDragKind: Record<Exclude<DragKind, 'Group'>, ItemGesture> = {
  Card: 'card',
  Bucket: 'bucket',
  Resize: 'resize',
  Image: 'image',
  ImageResize: 'image-resize',
}
/** Drags others are told about (not drawings, yet). */
const toDragKind = Object.fromEntries(Object.entries(fromDragKind).map(([k, v]) => [v, k])) as Partial<Record<ItemGesture, DragKind>>

/** Where in a card's title text a screen point is, as a character offset; undefined when it isn't on the text. */
/** Whether a clip's server is the one this page is on (its scheme may differ behind a proxy). */
function sameHost(url: string) {
  try {
    return new URL(url).host === window.location.host
  } catch {
    return false
  }
}

/** Whether text on the page is selected, which Ctrl/Cmd+C then copies as usual instead of the selected cards. */
const textSelected = () => (window.getSelection()?.toString().trim() ?? '') !== ''

function caretAt(x: number, y: number): number | undefined {
  const position = document.caretPositionFromPoint?.(x, y)
  const node = position?.offsetNode ?? document.caretRangeFromPoint?.(x, y)?.startContainer
  const offset = position?.offset ?? document.caretRangeFromPoint?.(x, y)?.startOffset
  const title = node?.parentElement?.closest('.card-title')
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
  /** Viewers can look around and open cards, but not change anything. */
  const readOnly = board.yourRole === 'Viewer'
  const [tool, setTool] = useState<Tool>('select')
  const [fill, setFill] = useState<CardColor>('Butter')
  /** The pen's ink and size, kept for the next visit. */
  const { ink, setInk, width: strokeWidth, setWidth: setStrokeWidth } = usePen(BOARD_PEN)
  /** The ring under the pointer that shows the pen's size. */
  const penRing = useRef<HTMLDivElement>(null)
  /** The strokes being drawn with the pen and saved; while it is the tool, they all go into one drawing. */
  const pen = useStroke((e) => toCanvas(e), tool === 'pen')
  /** Whether a stroke is being drawn or saved. */
  const stroke = pen.strokes.length > 0
  /** The selected items: one, or several (Shift or Ctrl/Cmd + click, or the selection rectangle). */
  const [selected, setSelected] = useState<Selection[]>([])
  /** The one item selected, for what works on a single item (its panel, its resize handles); null with none or several. */
  const selection = selected.length === 1 ? selected[0] : null
  const setSelection = useCallback((item: Selection | null) => setSelected(item ? [item] : []), [])
  const isSelected = (kind: SelectionKind, id: string) => isPicked(selected, kind, id)
  /** Takes an item out of the selection (it was deleted). */
  const unselect = (id: string) => setSelected((list) => (list.some((s) => s.id === id) ? list.filter((s) => s.id !== id) : list))
  /** Whether someone else is dragging an item right now, by itself or with others. */
  const remoteMoving = (id: string) => remoteDrags.some((r) => !r.endedAt && (r.id === id || r.items?.includes(id)))
  /** Space is held: a left press pans, as the middle button does. */
  const spaceHeld = useSpaceHeld()
  /** The opened card. A link to a card (/#/boards/{shortId}/cards/{number}) opens it when the board loads. */
  const [expanded, setExpanded] = useState<{ id: string; fresh: boolean } | null>(() => {
    const id = cardInHash(board)
    return id ? { id, fresh: false } : null
  })
  /** The tag or people window opened from a card on the board, next to the part that was clicked (screen pixels). */
  const [quickEdit, setQuickEdit] = useState<{ cardId: string; kind: 'tags' | 'people'; anchor: DOMRect } | null>(null)
  /** Tags to filter on: cards with none of them fade. Not kept: every board starts unfiltered. */
  const [tagFilter, setTagFilter] = useState<string[]>([])
  /** A tag color under the pointer in the palette, and one being saved: both show on the tag everywhere. */
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
  /** The card to scroll to instead of the search result: one put back from the trash. */
  const revealId = useRef<string | null>(null)
  /** Where a smooth scroll to a result is heading: quick key repeats are measured from there, not from a frame halfway. */
  const revealTarget = useRef<{ left: number; top: number; until: number } | null>(null)
  /** The search, hints and view-only badge at the top of the board. */
  const boardTop = useRef<HTMLDivElement>(null)
  /** The card whose title is being edited in place, and where the text cursor starts (default: the end). */
  const [titleEdit, setTitleEdit] = useState<{ id: string; caret?: number } | null>(null)
  /** The card whose title the current press started on: a click there (no drag) edits the title. */
  const titlePress = useRef<string | null>(null)
  /**
   * The cards the last two presses were on (null: something else). The canvas holds the pointer during a press, so the
   * browser sends clicks and double-clicks to the canvas, not to the card; a double-click on one card expands it.
   */
  const cardPresses = useRef<[string | null, string | null]>([null, null])
  const [gesture, setGesture] = useState<Gesture | null>(null)
  /** Whether the drag in progress has become a shake (cards shaken together line up in a grid). */
  const shaken = useRef(shakeDetector())
  const [pan, setPan] = useState<Pan | null>(null)
  /** A drag in progress on the open card's canvas. */
  const [cardBusy, setCardBusy] = useState(false)
  /** A board image being dragged over the open card, to attach it there. */
  const [incoming, setIncoming] = useState<IncomingImage | null>(null)
  /** Likewise a board drawing, to put it on the card's canvas. */
  const [incomingDrawing, setIncomingDrawing] = useState<IncomingDrawing | null>(null)
  /** Where a card (or several selected cards) being dragged would be linked, instead of moved. */
  const [linkDrop, setLinkDrop] = useState<LinkDrop | null>(null)
  /** Where several selected images and drawings being dragged would be attached, instead of moved. */
  const [attachDrop, setAttachDrop] = useState<AttachDrop | null>(null)
  /**
   * Where several selected cards being dragged over a bucket would go into it. Kept after the drop until the gesture
   * ends, so they don't jump back while the request is in flight.
   */
  const [arrangeState, setArrangeDrop] = useState<ArrangeDrop | null>(null)
  const arrangeDrop = gesture?.kind === 'group' ? arrangeState : null
  /** A new card being dragged from the tool rail: the pointer, in screen pixels. */
  const [newCard, setNewCard] = useState<Point | null>(null)
  /**
   * While a card or several selected items are dragged (or a new card carried), the rail's card button is a trash
   * can: dropping them on it deletes them, dropping a new one there puts it back. Whether the pointer is over it, and
   * what was dropped there, hidden until the reload takes it away.
   */
  const trashRef = useRef<HTMLButtonElement>(null)
  const [overTrash, setOverTrash] = useState(false)
  const [binned, setBinned] = useState<string[]>([])
  const expandedRef = useRef<ExpandedCardHandle>(null)
  /** The trash window: cards deleted in the last 7 days (opened from the board menu, through the address). */
  const [deletedOpen, setDeletedOpen] = useState(() => !board.visiting && trashInHash())
  const deletedRef = useRef<TrashWindowHandle>(null)
  /** A card in the trash shown as it is inside, read only. */
  const [deletedPreview, setDeletedPreview] = useState<{ card: CardDetail; images: ImageDetail[] } | null>(null)
  /** A card being dragged out of the trash window. */
  const [outgoing, setOutgoing] = useState<OutgoingCard | null>(null)
  /** A card dragged on the board is over the open trash (released there, it is deleted): the pointer, in screen pixels. */
  const [deletedDrop, setDeletedDrop] = useState<Point | null>(null)
  /** A drag in progress in the trash window. */
  const [deletedBusy, setDeletedBusy] = useState(false)
  /** The collapsed bucket a card (or several selected cards) being dragged would go into on release. */
  const [foldDrop, setFoldDrop] = useState<string | null>(null)
  /** A card or group being dragged is back over where it started (`home`): released there, nothing changes. */
  const [homeDrop, setHomeDrop] = useState(false)
  /** Buckets as they stack, the same for everyone (`stackedAt`): drawn in this order, the last one on top. */
  const stacked = backToFront(board.buckets)
  /**
   * A bucket just opened, pressed or moved here, shown in front before the server has put it on top for everyone (and
   * for viewers, who can't, only here). `over` is the bucket on top when it was picked: once that changes, the server's
   * order says what is in front again.
   */
  const [picked, setPicked] = useState<{ id: string; over: string | undefined } | null>(null)
  const top = stacked.at(-1)?.id
  /**
   * The bucket drawn above the others with what is in it, so a bucket over others isn't a tangle of cards: the topmost
   * open one. While selected cards are dragged into a bucket, that one, so how it would grow over its neighbours shows.
   */
  const front =
    board.buckets.find((b) => b.id === (arrangeDrop?.bucket.id ?? (picked?.over === top ? picked?.id : undefined)) && !b.collapsed)?.id ??
    stacked.findLast((b) => !b.collapsed)?.id ??
    null
  /**
   * Brings a bucket to the front: here at once, and for everyone through the server unless `server` says it does that
   * itself (moving, opening or filling it) or it is on top already.
   */
  function toFront(id: string, server = true) {
    // A press and the click after it ask once.
    const asked = picked?.id === id && picked.over === top
    setPicked({ id, over: top })
    if (server && !readOnly && top !== id && !asked) void run(() => api.bringBucketToFront(id))
  }
  /** Whether an item is drawn in front, with its bucket. */
  const inFront = (item: { bucketId: string | null }) => front !== null && item.bucketId === front
  /**
   * Whether the front bucket hides a canvas point from what isn't in it (`bucketId`, or a collapsed bucket's own id): its
   * fill covers the buckets, collapsed squares, cards and images under it, so a drop there doesn't reach them.
   */
  function coveredAt(p: Point, bucketId: string | null): boolean {
    const bucket = front !== null && bucketId !== front ? board.buckets.find((b) => b.id === front) : undefined
    if (!bucket) return false
    return contains(bucketRect(bucket), p)
  }
  /**
   * A card put at a point, naming the bucket in front when it covers the card's centre there: by position alone the
   * server would put it in the bucket stacked on top, which this screen may show behind the front one.
   */
  const cardPlace = (at: Point): Point & { bucketId?: string } =>
    front && bucketUnderCard(stacked.map((b) => ({ ...b, ...bucketRect(b) })), at, null, front)?.id === front
      ? { ...at, bucketId: front }
      : at
  /** Cards in a collapsed bucket are folded away with it: not on the board until it opens again. */
  const collapsedIds = new Set(board.buckets.filter((b) => b.collapsed).map((b) => b.id))
  const visibleCards = board.cards.filter((n) => !n.bucketId || !collapsedIds.has(n.bucketId))
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
   * searching there is more room above, so a card at the top can be scrolled below the list of results.
   */
  const margin = { x: Math.round(view.width / 2), y: Math.round(Math.max(view.height / 2, search ? SEARCH_MARGIN : 0)) }
  /**
   * Where the canvas's top-left corner is in the scrolled area: the margin, and the tool rail's room on the left. The
   * board has no edge, so that corner is its leftmost and topmost item, or 0,0 (`low`, below); `origin` is where 0,0 is.
   */
  const corner = { x: margin.x + RAIL_INSET, y: margin.y }
  // The wheel zooms the board (Shift + wheel scrolls). A quick-edit window sits next to the card on screen; after
  // zooming it would point at nothing.
  const { zoom, zoomTo } = useZoom(scrollRef, corner, { onZoom: () => setQuickEdit(null), wheel: 'always' })
  /** A short confirmation at the bottom of the board (a link was added, an image went into a card, an undo). */
  const [toast, setToast] = useState<ToastMessage | null>(null)
  const clearToast = useCallback(() => setToast(null), [])
  const notify = (message: string | Omit<ToastMessage, 'id'>) =>
    setToast({ id: Date.now(), ...(typeof message === 'string' ? { text: message } : message) })
  const undo = useUndo({
    boardId: board.id,
    history: undoHistory,
    run,
    notify,
    moving: (id) => remoteMoving(id),
    onApplied: (entry, direction) => {
      // A card or item that is gone now closes and is no longer selected.
      for (const { target: t, before, after } of entry.changes) {
        if ((direction === 'undo' ? before : after) !== null) continue
        if (t.kind === 'card' && expanded?.id === t.id) setExpanded(null)
        unselect(t.id)
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

  // The address bar follows the opened card, so it can be bookmarked or shared. Replacing the entry doesn't fire
  // hashchange; a card that isn't (or no longer is) on the board drops out of the address.
  // An address in the older form (ids) is rewritten to the short one the same way.
  const openPath = (expanded && board.cards.find((n) => n.id === expanded.id)?.path) ?? null
  useEffect(() => {
    const hash = openPath ? openPath.slice(1) : deletedOpen ? trashPath(board.shortId).slice(1) : `#/boards/${board.shortId}`
    if (window.location.hash !== hash) history.replaceState(history.state, '', hash)
  }, [openPath, deletedOpen, board.shortId])

  // The browser tab names what you are looking at: the open card (PRIK-118 Its title), or else the board. Away from
  // the board (admin page, sad page, signed out) it says PrikBoard again.
  const openCard = expanded ? board.cards.find((n) => n.id === expanded.id) : undefined
  const tabTitle = openCard ? `${openCard.readableId} ${cardLabel(openCard)}` : board.name
  useEffect(() => {
    document.title = tabTitle
  }, [tabTitle])
  useEffect(() => () => void (document.title = 'PrikBoard'), [])

  // Following a link to another card on this board opens it (one on another board loads that board: App).
  useEffect(() => {
    function onHashChange() {
      if (!isBoard(board, boardRefFromHash())) return
      const id = cardInHash(board)
      setTitleEdit(null)
      setQuickEdit(null)
      setExpanded(id ? { id, fresh: false } : null)
      if (id) setSelection({ kind: 'card', id })
      // The trash is for members; visitors of a public board don't see it.
      if (trashInHash() && !board.visiting) setDeletedOpen(true)
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [board, setSelection])

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
  useEffect(
    () => setPaused(gesture !== null || stroke || cardBusy || deletedBusy),
    [gesture, stroke, cardBusy, deletedBusy, setPaused],
  )

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
      // The open card is a window of its own: keys typed in it are its own, and Escape closes it.
      if (e.target instanceof Element && e.target.closest('input, textarea, select, [role="menu"], [role="dialog"], [role="alertdialog"]')) return
      if (e.key === 'Escape') {
        if (expanded) return
        setTool('select')
        setSelection(null)
      } else if (
        (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'c'
        && selected.length > 0 && !textSelected()
      ) {
        // Viewers may copy too: the copies go on boards where they can edit.
        e.preventDefault()
        if (!e.repeat) copySelected()
      } else if (
        (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'x'
        && !readOnly && selected.length > 0 && !textSelected()
      ) {
        e.preventDefault()
        if (!e.repeat) copySelected(true)
      } else if (readOnly) {
        if (e.key.toLowerCase() === 'v') setTool('select')
      } else if ((e.key === 'Delete' || e.key === 'Backspace') && selected.length > 1) {
        // Several at once: buckets too (what is in them stays). The toast's Undo puts them all back.
        e.preventDefault()
        if (!e.repeat && !gesture) void deleteSelected()
      } else if ((e.key === 'Delete' || e.key === 'Backspace') && selection && selection.kind !== 'bucket') {
        // No question first: the toast that follows has an Undo button, and Ctrl/Cmd+Z works too.
        e.preventDefault()
        if (!e.repeat) ({ card: deleteCard, image: deleteImage, drawing: deleteDrawing })[selection.kind](selection.id)
      } else if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'd' && selection?.kind === 'card') {
        e.preventDefault()
        if (!e.repeat) duplicateCard(selection.id)
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
  // card gives way to the search.
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

  // Ctrl/Cmd+Z undoes, Ctrl/Cmd+Shift+Z or Ctrl+Y redoes: on the board and in the open card. In a field the browser
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
      if (readOnly || gesture || stroke || cardBusy) return
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

  // Pasting copied cards puts them on the board where the pointer was. Pasting an image puts it on the board there, or on
  // the open card when the pointer or focus is there.
  useEffect(() => {
    function onPaste(e: ClipboardEvent) {
      const inField = e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable="true"]')
      const clip = inField ? null : readClip(e.clipboardData)
      if (clip) {
        e.preventDefault()
        if (readOnly) notify({ text: "You can't add cards to this board.", tone: 'warn' })
        else pasteClip(clip)
        return
      }
      const files = imageFiles(e.clipboardData)
      if (readOnly || files.length === 0) return
      e.preventDefault()
      const card = expandedRef.current
      void addImages(
        files,
        expanded && card?.wantsPaste() ? { cardId: expanded.id, at: card.pastePoint() } : { at: lastPointer.current ?? undefined },
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

  /** Edits a card's fields, as one undo step. */
  const editCard = (card: CardDetail, changes: CardChanges) => settle(...ops.editCard(card, changes))

  /** Deletes a card, closing it if it was open. */
  function deleteCard(id: string) {
    const card = board.cards.find((n) => n.id === id)
    if (!card) return Promise.resolve(false)
    if (expanded?.id === id) setExpanded(null)
    setSelection(null)
    return settle(...ops.deleteCard(card))
  }

  /** Puts a card from the trash back on the board: where it was, or at `at` (dragged out of the trash window). */
  function putBack(card: CardDetail, at?: Point) {
    if (deletedPreview?.card.id === card.id) setDeletedPreview(null)
    void settle(
      () => api.restoreCard(card.id, at),
      (after) => step('Put back card', [created(target.card(after.id), snap.card(after))]),
    ).then((ok) => {
      if (!ok) return
      notify('Put back on the board')
      setSelection({ kind: 'card', id: card.id })
      // The board scrolls to where it went (out of the window's way) once it has reloaded.
      revealId.current = card.id
      revealPending.current = true
    })
  }

  /** Deletes a card in the trash for good. That can't be undone, so it records no step. */
  async function purge(card: CardDetail) {
    if (deletedPreview?.card.id === card.id) setDeletedPreview(null)
    const ok = await runRec(() => api.purgeCard(board.id, card.id))
    if (ok) notify('Deleted for good')
    return ok
  }

  /** Deletes everything in the trash for good (the trash window asks first). */
  async function emptyTrash() {
    setDeletedPreview(null)
    let purged = 0
    const ok = await runRec(async () => {
      purged = (await api.emptyTrash(board.id)).purged
    })
    if (ok) notify(purged === 1 ? '1 card deleted for good' : `${purged} cards deleted for good`)
    return ok
  }

  /** Where a card dragged out of the trash window would land on the board, or null when it isn't over the board. */
  function outgoingSpot(drag: OutgoingCard): Point | null {
    if (!drag.outside || expandedRef.current?.contains(drag.client)) return null
    const at = boardPoint(drag.client)
    return at ? snapPoint({ x: at.x - drag.grab.x, y: at.y - drag.grab.y }) : null
  }

  /** Releases a card dragged out of the trash window; false when it went nowhere. */
  function dropOut(drag: OutgoingCard): boolean {
    const at = outgoingSpot(drag)
    if (at) putBack(drag.card, at)
    return at !== null
  }

  /** Folds a bucket up into a square in its top-right corner, hiding its cards, or opens it again with them where they were. */
  function toggleCollapsed(bucket: BucketDetail) {
    const collapsed = !bucket.collapsed
    // What folds away with it is no longer selected. Opened, it comes to the front with its cards (the server puts it on top).
    if (collapsed) setSelected((list) => list.filter((s) => s.kind === 'bucket' || bucketOf(s) !== bucket.id))
    else toFront(bucket.id, false)
    void settle(
      () => api.updateBucket(bucket.id, { collapsed }),
      (after) => step(collapsed ? 'Collapse bucket' : 'Open bucket', [patched(target.bucket(bucket.id), snap.bucket(bucket), snap.bucket(after), ['collapsed'])]),
    )
  }

  /** The collapsed bucket whose square is under a canvas point (not behind the front bucket). */
  function foldAt(p: Point): BucketDetail | undefined {
    return stacked.findLast((b) => {
      if (!b.collapsed || coveredAt(p, b.id)) return false
      const r = collapsedRect(bucketRect(b))
      return p.x >= r.x && p.x < r.x + r.width && p.y >= r.y && p.y < r.y + r.height
    })
  }

  /** Copies a card with everything on it one grid step down and right of it, and selects the copy. */
  function duplicateCard(id: string) {
    const card = board.cards.find((n) => n.id === id)
    if (!card) return
    const at = snapPoint({ x: card.x + GRID, y: card.y + GRID })
    void settle(
      async () => {
        const pasted = await api.pasteItems(board.id, await api.copyItems(board.id, { cardIds: [card.id] }), at)
        setSelection({ kind: 'card', id: pasted.cards[0].id })
        return pasted
      },
      (pasted) => step('Duplicate card', pasted.cards.map((copy) => created(target.card(copy.id), snap.card(copy)))),
    )
  }

  /**
   * Puts the selection on the clipboard, with everything on and in it (a bucket's items, a card's images), to paste on any
   * board (also on another server).
   */
  function copySelected(cut = false) {
    const ids = (kind: SelectionKind) => selected.filter((s) => s.kind === kind).map((s) => s.id)
    const what = { cardIds: ids('card'), bucketIds: ids('bucket'), imageIds: ids('image'), drawingIds: ids('drawing'), cut }
    // Cut items stay where they are until they are pasted: then they move there.
    writeClip(api.copyItems(board.id, what)).then(
      (clip) => notify(cut ? `Cut ${clipSummary(clip)}: paste to move it there` : `Copied ${clipSummary(clip)}`),
      (error) => notify({ text: `Couldn't ${cut ? 'cut' : 'copy'}: ${errorMessage(error)}`, tone: 'warn' }),
    )
  }

  /**
   * A cut pasted on the board it was cut from: the items still here move, as one step, so their top-left lands at
   * `at`. Cards keep their numbers, unlike a paste.
   */
  function moveCutHere(clip: BoardClip, at: Point) {
    const loose = (item: { bucketId?: string | null }) => !item.bucketId
    const tops = [
      ...(clip.buckets ?? []).map((b) => ({ kind: 'bucket' as const, ...b })),
      ...clip.cards.filter(loose).map((n) => ({ kind: 'card' as const, ...n })),
      ...clip.images.filter((i) => !i.cardId && loose(i)).map((i) => ({ kind: 'image' as const, ...i, id: i.id ?? '' })),
      ...clip.drawings.filter((d) => !d.cardId && loose(d)).map((d) => ({ kind: 'drawing' as const, ...d, id: d.id ?? '' })),
    ]
    const here = {
      bucket: board.buckets,
      card: board.cards,
      image: board.images,
      drawing: board.drawings,
    }
    const members = tops.filter((t) => here[t.kind].some((x) => x.id === t.id)).map((t): Selection => ({ kind: t.kind, id: t.id }))
    if (members.length === 0) {
      notify({ text: 'These were moved already.', tone: 'warn' })
      return
    }
    const from = snapPoint({ x: Math.min(...tops.map((t) => t.x)), y: Math.min(...tops.map((t) => t.y)) })
    const to = snapPoint(at)
    moveGroup(members, undefined, { x: to.x - from.x, y: to.y - from.y })
    setSelected(members)
  }

  /**
   * Adds copied items with their top-left where the pointer was (with no pointer yet, cards go to the first bucket and
   * anything else to a free spot), and selects them: the buckets and what isn't in them, as the selection rectangle would.
   */
  function pasteClip(clip: BoardClip) {
    const at = lastPointer.current
    if (clip.cut && clip.source.boardShortId === board.shortId && sameHost(clip.source.url)) {
      if (at) moveCutHere(clip, at)
      return
    }
    const [paste, describe] = ops.paste(board.id, clip, at ? snapPoint(at) : {})
    void settle(async () => {
      const pasted = await paste()
      const fresh = new Set(pasted.buckets.map((b) => b.id))
      const outside = (item: { bucketId: string | null }) => !item.bucketId || !fresh.has(item.bucketId)
      setSelected([
        ...pasted.buckets.map((b): Selection => ({ kind: 'bucket', id: b.id })),
        ...pasted.cards.filter(outside).map((n): Selection => ({ kind: 'card', id: n.id })),
        ...pasted.images.filter((i) => !i.cardId && outside(i)).map((i): Selection => ({ kind: 'image', id: i.id })),
        ...pasted.drawings.filter((d) => !d.cardId && outside(d)).map((d): Selection => ({ kind: 'drawing', id: d.id })),
      ])
      return pasted
    }, describe)
  }

  const renameTag = (tag: BoardTag, name: string) => settle(...ops.renameTag(board.id, tag, name))
  /** Recolors a tag; the new color shows right away and stays until the reloaded board has it. */
  async function recolorTag(tag: BoardTag, color: TagColor) {
    setTagSaving({ tagId: tag.id, color })
    const ok = await settle(...ops.recolorTag(board.id, tag, color))
    setTagSaving((s) => (s?.tagId === tag.id && s.color === color ? null : s))
    return ok
  }
  const deleteTag = (tag: BoardTag) => settle(...ops.deleteTag(board.id, tag))

  function deleteImage(id: string) {
    const image = board.images.find((i) => i.id === id)
    if (!image) return
    unselect(id)
    void settle(...ops.deleteImage(image))
  }

  function deleteDrawing(id: string) {
    const drawing = board.drawings.find((d) => d.id === id)
    if (!drawing) return
    unselect(id)
    void settle(
      () => api.deleteDrawing(id),
      () => step('Delete drawing', [deleted(target.drawing(id), snap.drawing(drawing))], { toast: 'Drawing deleted' }),
    )
  }

  function recolorDrawing(drawing: DrawingDetail, color: InkColor) {
    void settle(
      () => api.updateDrawing(drawing.id, { color }),
      (after) => step('Change ink', [patched(target.drawing(drawing.id), snap.drawing(drawing), snap.drawing(after), ['colors'])]),
    )
  }

  /** What kind of item an id is on this board, if it is on it. */
  function kindOf(id: string): SelectionKind | undefined {
    if (board.cards.some((n) => n.id === id)) return 'card'
    if (board.buckets.some((b) => b.id === id)) return 'bucket'
    if (board.images.some((i) => i.id === id)) return 'image'
    if (board.drawings.some((d) => d.id === id)) return 'drawing'
    return undefined
  }

  /** The bucket a selected card, image or drawing is in (null: loose). */
  function bucketOf(item: Selection): string | null {
    const list: { id: string; bucketId: string | null }[] =
      item.kind === 'card' ? board.cards : item.kind === 'image' ? board.images : item.kind === 'drawing' ? board.drawings : []
    return list.find((i) => i.id === item.id)?.bucketId ?? null
  }

  /** Everything a request in several changes did, as one step: what made it, when one of them fails. */
  async function runAll(label: string, toast: string | undefined, work: (changes: (UndoChange | null)[]) => Promise<unknown>) {
    const ok = await undo.track(
      run(async () => {
        const changes: (UndoChange | null)[] = []
        try {
          await work(changes)
        } finally {
          const entry = step(label, changes, { toast })
          if (entry) undo.record(entry)
        }
      }),
    )
    setGesture(null)
    return ok
  }

  /** Waits for all of them, then fails with the first failure, if any (the ones that worked still count). */
  async function allOf(requests: Promise<unknown>[]) {
    const failed = (await Promise.allSettled(requests)).find((r) => r.status === 'rejected')
    if (failed) throw failed.reason
  }

  const count = (n: number, one: string, many: string) => (n === 1 ? `1 ${one}` : `${n} ${many}`)

  /** Deletes the selected items at once, as one step; buckets go but what is in them stays. */
  function deleteSelected() {
    const items = selected
    if (items.some((s) => s.kind === 'card' && s.id === expanded?.id)) setExpanded(null)
    setSelected([])
    return runAll(`Delete ${count(items.length, 'item', 'items')}`, `${count(items.length, 'item', 'items')} deleted`, (changes) =>
      allOf(
        items.map(async ({ kind, id }) => {
          if (kind === 'card') {
            const card = board.cards.find((n) => n.id === id)
            if (!card) return
            await api.deleteCard(id)
            changes.push(deleted(target.card(id), snap.card(card)))
          } else if (kind === 'bucket') {
            const bucket = board.buckets.find((b) => b.id === id)
            if (!bucket) return
            await api.deleteBucket(id)
            changes.push(deleted(target.bucket(id), snap.bucket(bucket)))
          } else if (kind === 'image') {
            const image = board.images.find((i) => i.id === id)
            if (!image) return
            await api.deleteImage(id)
            changes.push(deleted(target.image(id), snap.image(image)))
          } else {
            const drawing = board.drawings.find((d) => d.id === id)
            if (!drawing) return
            await api.deleteDrawing(id)
            changes.push(deleted(target.drawing(id), snap.drawing(drawing)))
          }
        }),
      ),
    )
  }

  /**
   * The step's changes for a bucket that moved: the bucket, and the cards, images and drawings it carried along by
   * the same distance (undoing the move takes exactly those back).
   */
  function bucketMove(bucket: BucketDetail, moved: BucketDetail): (UndoChange | null)[] {
    const carried = board.cards.filter((n) => n.bucketId === bucket.id)
    const carriedImages = board.images.filter((i) => i.bucketId === bucket.id)
    const carriedDrawings = board.drawings.filter((d) => d.bucketId === bucket.id)
    const d = { x: moved.x - bucket.x, y: moved.y - bucket.y }
    const along = (item: Point) => ({ x: onBoard(item.x + d.x), y: onBoard(item.y + d.y) })
    const carry = { cards: carried.map((n) => n.id), images: carriedImages.map((i) => i.id), drawings: carriedDrawings.map((d) => d.id) }
    return [
      patched(target.bucket(bucket.id, carry), snap.bucket(bucket), snap.bucket(moved), ['x', 'y']),
      ...carried.map((n) => patched(target.card(n.id), { x: n.x, y: n.y }, along(n), ['x', 'y'], true)),
      ...carriedImages.map((i) => patched(target.image(i.id), { x: i.x, y: i.y }, along(i), ['x', 'y'], true)),
      ...carriedDrawings.map((dr) => patched(target.drawing(dr.id), { x: dr.x, y: dr.y }, along(dr), ['x', 'y'], true)),
    ]
  }

  /**
   * Where shaken cards go: a grid as square as it can be, one grid step apart, in the order they were read in (top
   * to bottom, then left to right), placed so the card held stays where it is. Undefined unless they are all
   * cards (two or more).
   */
  function gridFor(heldId: string, members: Selection[]): Record<string, Point> | undefined {
    if (members.length < 2 || members.some((m) => m.kind !== 'card')) return undefined
    const cards = members
      .flatMap((m) => board.cards.filter((n) => n.id === m.id))
      .toSorted((a, b) => a.y - b.y || a.x - b.x)
    const held = cards.find((n) => n.id === heldId)
    if (!held || cards.length < 2) return undefined
    const columns = Math.ceil(Math.sqrt(cards.length))
    const cell = (i: number) => ({ x: (i % columns) * (CARD_WIDTH + GRID), y: Math.floor(i / columns) * (CARD_HEIGHT + GRID) })
    const own = cell(cards.indexOf(held))
    return Object.fromEntries(cards.map((n, i) => [n.id, { x: held.x - own.x + cell(i).x, y: held.y - own.y + cell(i).y }]))
  }

  /**
   * Drops several items moved together, where the drag shows them (moved on `by`: a cut pasted here). Buckets go first,
   * carrying what is in them, so the loose items that follow land among them as they are shown.
   */
  function moveGroup(members: Selection[], label = `Move ${count(members.length, 'item', 'items')}`, by: Point = { x: 0, y: 0 }) {
    const groupBuckets = members.flatMap((m) => (m.kind === 'bucket' ? board.buckets.filter((b) => b.id === m.id) : []))
    const groupCards = members.flatMap((m) => (m.kind === 'card' ? board.cards.filter((n) => n.id === m.id) : []))
    const groupImages = members.flatMap((m) => (m.kind === 'image' ? board.images.filter((i) => i.id === m.id) : []))
    const groupDrawings = members.flatMap((m) => (m.kind === 'drawing' ? board.drawings.filter((d) => d.id === m.id) : []))
    // Where they go, worked out now: the drag ends with the first reply.
    const spots = new Map<string, Point>([
      ...groupBuckets.map((b) => [b.id, bucketRect(b)] as const),
      ...groupCards.map((n) => [n.id, cardPoint(n)] as const),
      ...groupImages.map((i) => [i.id, imageRect(i)] as const),
      ...groupDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    const to = (id: string) => ({ x: spots.get(id)!.x + by.x, y: spots.get(id)!.y + by.y })
    void runAll(label, undefined, async (changes) => {
      await allOf(groupBuckets.map(async (b) => changes.push(...bucketMove(b, await api.updateBucket(b.id, to(b.id))))))
      await allOf([
        ...groupCards.map(async (n) => {
          const moved = await api.moveCard(n.id, cardPlace(to(n.id)))
          changes.push(patched(target.card(n.id), snap.card(n), snap.card(moved), ['x', 'y']))
        }),
        ...groupImages.map(async (i) => {
          const moved = await api.updateImage(i.id, to(i.id))
          changes.push(patched(target.image(i.id), snap.image(i), snap.image(moved), ['x', 'y']))
        }),
        ...groupDrawings.map(async (d) => {
          const moved = await api.updateDrawing(d.id, to(d.id))
          changes.push(patched(target.drawing(d.id), snap.drawing(d), snap.drawing(moved), ['x', 'y']))
        }),
      ])
    })
  }

  /**
   * Puts cards into a bucket's free spots in one request (the server finds them, growing the bucket towards `grow` when
   * they don't fit), after moving the images and drawings that came along to where they were dropped. One step.
   */
  function putCards(bucket: BucketDetail, cards: CardDetail[], grow: GrowSide, along: Selection[]) {
    const alongImages = along.flatMap((m) => (m.kind === 'image' ? board.images.filter((i) => i.id === m.id) : []))
    const alongDrawings = along.flatMap((m) => (m.kind === 'drawing' ? board.drawings.filter((d) => d.id === m.id) : []))
    const spots = new Map<string, Point>([
      ...alongImages.map((i) => [i.id, imageRect(i)] as const),
      ...alongDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    const to = (id: string) => ({ x: spots.get(id)!.x, y: spots.get(id)!.y })
    return runAll(`Put ${count(cards.length, 'card', 'cards')} in ${bucket.name}`, undefined, async (changes) => {
      await allOf([
        ...alongImages.map(async (i) => {
          const moved = await api.updateImage(i.id, to(i.id))
          changes.push(patched(target.image(i.id), snap.image(i), snap.image(moved), ['x', 'y']))
        }),
        ...alongDrawings.map(async (d) => {
          const moved = await api.updateDrawing(d.id, to(d.id))
          changes.push(patched(target.drawing(d.id), snap.drawing(d), snap.drawing(moved), ['x', 'y']))
        }),
      ])
      const placed = await api.placeCardsInBucket(bucket.id, { cardIds: cards.map((n) => n.id), grow })
      // Undone, the cards go back first, then the bucket shrinks to its old size.
      changes.push(patched(target.bucket(bucket.id), snap.bucket(bucket), snap.bucket(placed.bucket), ['x', 'y', 'width', 'height']))
      for (const after of placed.cards) {
        const before = cards.find((n) => n.id === after.id)
        if (before) changes.push(patched(target.card(after.id), snap.card(before), snap.card(after), ['x', 'y']))
      }
    })
  }

  /**
   * Lays several selected cards out as a grid where they are, in the order asked: from the selection's top-left, as
   * many columns as fit its width. One step.
   */
  function sortCards(sort: CardSort) {
    const cards = selected.flatMap((s) => (s.kind === 'card' ? board.cards.filter((n) => n.id === s.id) : []))
    if (cards.length < 2) return
    const tagRank = (card: CardDetail) => {
      const names = board.tags.map((t) => t.name)
      const ranks = card.tags.map((t) => names.indexOf(t)).filter((i) => i >= 0)
      return ranks.length > 0 ? Math.min(...ranks) : Number.MAX_SAFE_INTEGER
    }
    const order: Record<CardSort, (a: CardDetail, b: CardDetail) => number> = {
      number: (a, b) => a.number - b.number,
      // Untitled cards last.
      title: (a, b) => Number(!a.title.trim()) - Number(!b.title.trim()) || a.title.localeCompare(b.title) || a.number - b.number,
      tag: (a, b) => tagRank(a) - tagRank(b) || a.number - b.number,
      // In the order the fill picker shows them.
      color: (a, b) => cardColors.indexOf(a.color) - cardColors.indexOf(b.color) || a.number - b.number,
      edited: (a, b) => b.updatedAt.localeCompare(a.updatedAt),
    }
    const sorted = cards.toSorted(order[sort])
    const left = Math.min(...cards.map((n) => n.x))
    const top = Math.min(...cards.map((n) => n.y))
    const right = Math.max(...cards.map((n) => n.x + CARD_WIDTH))
    const columns = Math.max(1, Math.floor((right - left + GRID) / (CARD_WIDTH + GRID)))
    const spot = (i: number) => snapPoint({ x: left + (i % columns) * (CARD_WIDTH + GRID), y: top + Math.floor(i / columns) * (CARD_HEIGHT + GRID) })
    void runAll(`Sort ${count(cards.length, 'card', 'cards')}`, undefined, (changes) =>
      allOf(
        sorted.map(async (card, i) => {
          const moved = await api.moveCard(card.id, cardPlace(spot(i)))
          changes.push(patched(target.card(card.id), snap.card(card), snap.card(moved), ['x', 'y']))
        }),
      ),
    )
  }

  /**
   * What the selection rectangle takes: over the board, the buckets and loose items it touches; inside a bucket, that
   * bucket's own items it touches. Folded-away items aren't on the board to take.
   */
  function inRectangle(rect: Rect, bucketId: string | null): Selection[] {
    const loose = (item: { bucketId: string | null }) => item.bucketId === bucketId
    return [
      ...(bucketId
        ? []
        : board.buckets
            .filter((b) => touches(rect, b.collapsed ? collapsedRect(b) : b))
            .map((b) => ({ kind: 'bucket' as const, id: b.id }))),
      ...visibleCards
        .filter((n) => loose(n) && touches(rect, { x: n.x, y: n.y, width: CARD_WIDTH, height: CARD_HEIGHT }))
        .map((n) => ({ kind: 'card' as const, id: n.id })),
      ...images.filter(({ image, rect: r }) => loose(image) && touches(rect, r)).map(({ image }) => ({ kind: 'image' as const, id: image.id })),
      ...drawings
        .filter(({ drawing, rect: r }) => loose(drawing) && touches(rect, r))
        .map(({ drawing }) => ({ kind: 'drawing' as const, id: drawing.id })),
    ]
  }

  /** The middle of what is on screen, in canvas pixels. */
  function viewportCenter(): Point {
    return { x: (view.x - origin.x + view.width / 2) / zoom, y: (view.y - origin.y + view.height / 2) / zoom }
  }

  /**
   * Uploads images one after another, cascading so they don't cover each other: onto the board around `at` (default:
   * the middle of the screen), or attached to a card with their top-left at `at` on its canvas (default: a free spot).
   * A single image on the board is selected afterwards.
   */
  async function addImages(files: File[], place: { at?: Point; cardId?: string }) {
    if (files.length === 0) return
    const at = place.at ?? viewportCenter()
    setUploads((n) => n + 1)
    await undo.track(
      run(async () => {
        const added: ImageDetail[] = []
        try {
          for (const [i, file] of files.entries()) {
            const placement = place.cardId
              ? { cardId: place.cardId, ...(place.at ? snapPoint({ x: place.at.x + i * 40, y: place.at.y + i * 40 }) : {}) }
              : snapPoint({ x: at.x - IMAGE_OFFSET.x + i * 40, y: at.y - IMAGE_OFFSET.y + i * 40 })
            added.push(await api.uploadImage(board.id, file, placement))
          }
        } finally {
          // One step for all of them; when one fails, the ones that made it.
          const entry = step(added.length === 1 ? 'Add image' : 'Add images', added.map((image) => created(target.image(image.id), snap.image(image))))
          if (entry) undo.record(entry)
        }
        if (added.length === 1 && !place.cardId) setSelection({ kind: 'image', id: added[0].id })
      }),
    )
    setUploads((n) => n - 1)
  }

  async function addImagesFromPicker(target: { cardId?: string } = {}) {
    await addImages(await pickImageFiles(), target)
  }

  // Live geometry: the server's layout, with the drags in progress applied on top (yours wins over someone else's).
  const dragging = gesture && gesture.kind !== 'draw' && gesture.kind !== 'select' && isDrag(gesture) ? gesture : null
  /**
   * Several items moving together each move by the same distance; cards lined up in a grid go to their place in it,
   * moving on with the pointer, and cards going into a bucket (`spots`) wait in theirs.
   */
  const groupDrags = (members: Selection[], d: Point, grid?: Record<string, Point>, spots?: Record<string, Point>): ActiveDrag[] =>
    members.map(({ kind, id }) => {
      const spot = spots?.[id]
      const cell = spot ?? grid?.[id]
      const card = cell && board.cards.find((n) => n.id === id)
      const along = spot ? { x: 0, y: 0 } : d
      return { kind, id, d: card ? { x: cell.x - card.x + along.x, y: cell.y - card.y + along.y } : d, handle: 'BottomRight' }
    })
  const drags: ActiveDrag[] = [
    ...(!dragging
      ? []
      : dragging.kind === 'group'
        ? groupDrags(dragging.members, delta(dragging), dragging.grid, arrangeDrop?.spots)
        : [{ kind: dragging.kind, id: dragging.id, d: delta(dragging), handle: dragging.handle ?? 'BottomRight' }]),
    ...remoteDrags.flatMap((r): ActiveDrag[] => {
      const d = { x: r.dx, y: r.dy }
      if (r.kind !== 'Group') return [{ kind: fromDragKind[r.kind], id: r.id, d, handle: r.handle ?? 'BottomRight' }]
      return groupDrags((r.items ?? []).flatMap((id) => (kindOf(id) ? [{ kind: kindOf(id)!, id }] : [])), d)
    }),
  ]
  const dragOf = (id: string | null) => (id === null ? undefined : drags.find((g) => g.id === id))
  /** Whether an item is lifted by your own drag in progress: by itself, or with the others selected. */
  const lifting = (id: string) =>
    !!dragging && !dragging.done && (dragging.kind === 'group' ? dragging.members.some((m) => m.id === id) : dragging.id === id)

  // Tell the others about your drag while it is in progress (as a string, so an unchanged drag sends nothing).
  const sentDrag = !dragging || dragging.done
    ? null
    : dragging.kind === 'group'
      ? JSON.stringify({ kind: 'Group', id: dragging.id, dx: delta(dragging).x, dy: delta(dragging).y, items: dragging.members.map((m) => m.id) })
      : toDragKind[dragging.kind]
        ? JSON.stringify({ kind: toDragKind[dragging.kind], id: dragging.id, dx: delta(dragging).x, dy: delta(dragging).y, handle: dragging.handle })
        : null
  useEffect(() => onDrag?.(sentDrag ? (JSON.parse(sentDrag) as DragUpdate) : null), [sentDrag, onDrag])

  function bucketRect(bucket: BucketDetail): Rect {
    // Cards going into it show how it would grow to fit them.
    if (arrangeDrop?.bucket.id === bucket.id && dragging) return arrangeDrop.grown
    const drag = dragOf(bucket.id)
    if (!drag) return bucket
    const { d } = drag
    if (drag.kind === 'bucket') return { ...bucket, ...snapPoint({ x: bucket.x + d.x, y: bucket.y + d.y }) }
    if (drag.kind === 'resize') return { ...bucket, ...resizeFrom(bucket, drag.handle, d, bucketSize, MIN_COORDINATE) }
    return bucket
  }

  function cardPoint(card: CardDetail): Point {
    const drag = dragOf(card.id)
    if (drag?.kind === 'card') return snapPoint({ x: card.x + drag.d.x, y: card.y + drag.d.y })
    if (dragOf(card.bucketId)?.kind === 'bucket') {
      // A moving bucket carries its cards along, as the server does.
      const bucket = board.buckets.find((b) => b.id === card.bucketId)!
      const moved = bucketRect(bucket)
      return { x: onBoard(card.x + moved.x - bucket.x), y: onBoard(card.y + moved.y - bucket.y) }
    }
    return card
  }

  function imageRect(image: ImageDetail): Rect {
    const drag = dragOf(image.id)
    if (!drag) {
      // A moving bucket carries its images along, as the server does.
      const bucket = dragOf(image.bucketId)?.kind === 'bucket' && board.buckets.find((b) => b.id === image.bucketId)
      if (!bucket) return image
      const moved = bucketRect(bucket)
      return { ...image, x: onBoard(image.x + moved.x - bucket.x), y: onBoard(image.y + moved.y - bucket.y) }
    }
    const { d } = drag
    if (drag.kind === 'image') return { ...image, ...snapPoint({ x: image.x + d.x, y: image.y + d.y }) }
    if (drag.kind === 'image-resize') {
      const resized = resizeImage(image, drag.handle, d, MIN_COORDINATE)
      return { ...image, ...resized, ...snapPoint(resized) }
    }
    return image
  }

  function drawingRect(drawing: DrawingDetail): Rect {
    const drag = dragOf(drawing.id)
    if (drag?.kind === 'drawing') return { ...drawing, ...snapPoint({ x: drawing.x + drag.d.x, y: drawing.y + drag.d.y }) }
    // A moving bucket carries its drawings along, as the server does.
    const bucket = dragOf(drawing.bucketId)?.kind === 'bucket' && board.buckets.find((b) => b.id === drawing.bucketId)
    if (!bucket) return drawing
    const moved = bucketRect(bucket)
    return { ...drawing, x: onBoard(drawing.x + moved.x - bucket.x), y: onBoard(drawing.y + moved.y - bucket.y) }
  }

  /** Where an item on the board is shown right now (moving or not); null when it isn't on the board. */
  function liveRect(kind: SelectionKind, id: string): Rect | null {
    if (kind === 'card') {
      const card = board.cards.find((n) => n.id === id)
      return card ? { ...cardPoint(card), width: CARD_WIDTH, height: CARD_HEIGHT } : null
    }
    if (kind === 'bucket') {
      const bucket = board.buckets.find((b) => b.id === id)
      if (!bucket) return null
      return bucket.collapsed ? collapsedRect(bucketRect(bucket)) : bucketRect(bucket)
    }
    if (kind === 'drawing') {
      const drawing = board.drawings.find((d) => d.id === id && d.cardId === null)
      return drawing ? drawingRect(drawing) : null
    }
    const image = board.images.find((i) => i.id === id && i.cardId === null)
    return image ? imageRect(image) : null
  }

  /** Where someone else's drag is right now, to outline it with their name: one item, or each of several moving together. */
  function remoteRects(drag: LiveDrag): Rect[] {
    if (drag.kind !== 'Group') {
      const kind = fromDragKind[drag.kind]
      const rect = liveRect(kind === 'resize' ? 'bucket' : kind === 'image-resize' ? 'image' : (kind as SelectionKind), drag.id)
      return rect ? [rect] : []
    }
    return (drag.items ?? []).flatMap((id) => {
      const kind = kindOf(id)
      const rect = kind && liveRect(kind, id)
      return rect ? [rect] : []
    })
  }

  function toCanvas(e: { clientX: number; clientY: number }): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: (e.clientX - rect.left) / zoom, y: (e.clientY - rect.top) / zoom }
  }

  /** A screen point in canvas pixels when it is over the board (and not over the open card); else null. */
  function boardPoint(client: Point): Point | null {
    const rect = scrollRef.current!.getBoundingClientRect()
    const inside = client.x >= rect.left && client.x < rect.right && client.y >= rect.top && client.y < rect.bottom
    return inside ? toCanvas({ clientX: client.x, clientY: client.y }) : null
  }

  /** Cards in the order they are drawn: the front bucket's above the rest, then the most recently updated on top. */
  const drawOrder = (a: CardDetail, b: CardDetail) => Number(inFront(a)) - Number(inFront(b)) || a.updatedAt.localeCompare(b.updatedAt)

  /**
   * The topmost card on the board under a canvas point, other than `except` (one id, or the ids of a group). Not one
   * the front bucket hides there.
   */
  function cardAt(p: Point, except?: string | Set<string>): CardDetail | undefined {
    const skip = (id: string) => (typeof except === 'string' ? id === except : !!except?.has(id))
    return visibleCards
      .toSorted(drawOrder)
      .findLast(
        (n) => !skip(n.id) && !coveredAt(p, n.bucketId) && p.x >= n.x && p.x < n.x + CARD_WIDTH && p.y >= n.y && p.y < n.y + CARD_HEIGHT,
      )
  }

  /**
   * Where a card being dragged would be linked when released at a screen point: the open card (anywhere over its
   * window), or the card under the pointer on the board. Several selected cards dragged together link the same way,
   * to a card that isn't one of them. Null for other drags, or over nothing to link to.
   */
  function linkDropFor(g: Gesture, client: Point): LinkDrop | null {
    const dragged =
      g.kind === 'card' ? new Set([g.id]) : g.kind === 'group' && g.members.every((m) => m.kind === 'card') ? new Set(g.members.map((m) => m.id)) : null
    if (!dragged) return null
    const win = expandedRef.current
    if (expanded && win?.contains(client)) {
      const open = !dragged.has(expanded.id) ? board.cards.find((n) => n.id === expanded.id) : undefined
      return open ? { card: open, window: true, at: win.hits(client) ? win.toCard(client) : undefined, client } : null
    }
    const under = cardAt(toCanvas({ clientX: client.x, clientY: client.y }), dragged)
    return under ? { card: under, window: false, client } : null
  }

  /**
   * Adds links to `linked` cards on a card: into the links component at `at` on its canvas, else its first links
   * component; only a card without one gets a new links component (at `at`, or in a free spot). Cards it already links
   * to are left out; the undo step (null when nothing changed) and how many links were added.
   */
  async function linkCards({ card, at }: LinkDrop, linked: CardDetail[]): Promise<{ entry: UndoEntry | null; added: number }> {
    const links = linked
      .filter((l) => l.id !== card.id && !card.components.some((b) => b.links?.some((x) => linksTo(x.url, l))))
      .map((l) => ({ title: cardLabel(l), url: l.path }))
    if (links.length === 0) return { entry: null, added: 0 }
    const label = links.length === 1 ? 'Link card' : 'Link cards'
    const lists = card.components.filter((b) => b.kind === 'Links')
    const component =
      (at && lists.find((b) => at.x >= b.x && at.x < b.x + b.width && at.y >= b.y && at.y < b.y + b.height)) ?? lists[0]
    if (component) {
      const updated = await api.updateCardComponent(card.id, component.id, { links: [...(component.links ?? []), ...links] })
      const entry = step(label, [patched(target.component(card.id, component.id), snap.component(component), snap.component(updated), ['links'])])
      return { entry, added: links.length }
    }
    const place = at ? onCardCanvas({ x: at.x - GRID, y: at.y - GRID }, defaultComponentSize('Links')) : {}
    const added = await api.addCardComponent(card.id, { kind: 'Links', links, ...place })
    return { entry: step(label, [created(target.component(card.id, added.id), snap.component(added))]), added: links.length }
  }

  /**
   * Where several selected images and drawings being dragged would be attached when released at a screen point: the
   * open card (over its canvas) or the card under the pointer on the board. Null unless the group is only images and
   * drawings.
   */
  function arrangeFor(g: Gesture): ArrangeDrop | null {
    if (g.kind !== 'group' || g.members.some((m) => m.kind === 'bucket')) return null
    const cards = g.members
      .flatMap((m) => (m.kind === 'card' ? board.cards.filter((n) => n.id === m.id) : []))
      .toSorted((a, b) => a.y - b.y || a.x - b.x)
    const p = g.current
    // The front bucket where it covers the pointer, else the topmost one there.
    const under = stacked.filter((b) => !b.collapsed && p.x >= b.x && p.x < b.x + b.width && p.y >= b.y && p.y < b.y + b.height)
    const bucket = under.find((b) => b.id === front) ?? under.at(-1)
    // Moved around inside the bucket they are all in already, they go where they are dropped.
    if (cards.length === 0 || !bucket || cards.every((n) => n.bucketId === bucket.id)) return null
    const edges: [GrowSide, number][] = [
      ['Left', p.x - bucket.x],
      ['Right', bucket.x + bucket.width - p.x],
      ['Up', p.y - bucket.y],
      ['Down', bucket.y + bucket.height - p.y],
    ]
    const side = edges.reduce((a, b) => (b[1] < a[1] ? b : a))[0]
    // What is in the way: the bucket's own cards and images and loose ones, not its neighbours' (they join it by id, so
    // it may grow over those, only as far as needed), and the group's images where they are dropped (they move first).
    const ids = new Set(g.members.map((m) => m.id))
    const inTheWay = (owner: string | null) => owner === null || owner === bucket.id
    const taken = [
      ...board.cards
        .filter((n) => !ids.has(n.id) && inTheWay(n.bucketId))
        .map((n) => ({ x: n.x, y: n.y, width: CARD_WIDTH, height: CARD_HEIGHT })),
      ...board.images.filter((i) => i.cardId === null && !ids.has(i.id) && inTheWay(i.bucketId)),
      ...board.images.filter((i) => ids.has(i.id)).map(imageRect),
    ]
    const { bucket: grown, spots } = packIn(bucket, cards.length, taken, side)
    return { bucket, side, grown, spots: Object.fromEntries(cards.map((n, i) => [n.id, spots[i]])) }
  }

  function attachDropFor(g: Gesture, client: Point): AttachDrop | null {
    if (g.kind !== 'group' || !g.members.every((m) => m.kind === 'image' || m.kind === 'drawing')) return null
    const win = expandedRef.current
    if (expanded && win?.contains(client)) {
      const open = board.cards.find((n) => n.id === expanded.id)
      return open && win.hits(client) ? { card: open, window: true } : null
    }
    const under = cardAt(toCanvas({ clientX: client.x, clientY: client.y }))
    return under ? { card: under, window: false } : null
  }

  const cardElement = (id: string) => canvasRef.current?.querySelector(`[data-card="${id}"]`)

  /**
   * An image dropped on a card on the board is sucked into it: it shrinks into the card's middle and fades, then
   * the card gives a little gulp. The image stays hidden (until the reload takes it off the board) unless cancelled.
   */
  function suckInto(imageId: string, card: CardDetail): Animation | undefined {
    const el = canvasRef.current?.querySelector(`[data-image="${imageId}"]`)
    const image = board.images.find((i) => i.id === imageId)
    if (!el || !image) return undefined
    const from = imageRect(image)
    const to = cardPoint(card)
    const dx = to.x + CARD_WIDTH / 2 - (from.x + from.width / 2)
    const dy = to.y + CARD_HEIGHT / 2 - (from.y + from.height / 2)
    const keyframes = reducedMotion()
      ? [{ opacity: 0.6 }, { opacity: 0 }]
      : [
          { transform: 'translate(0, 0) scale(1)', opacity: 0.6 },
          { transform: `translate(${dx * 0.7}px, ${dy * 0.7}px) scale(0.35)`, opacity: 0.5, offset: 0.6 },
          { transform: `translate(${dx}px, ${dy}px) scale(0.02)`, opacity: 0 },
        ]
    const animation = el.animate(keyframes, { duration: 420, easing: 'cubic-bezier(0.5, 0, 0.75, 0)', fill: 'forwards' })
    animation.onfinish = () => pop(cardElement(card.id), 1.06)
    return animation
  }

  /**
   * Where a board image being dragged would land on the open card's canvas (keeping the point it was grabbed at
   * under the pointer), or null when the pointer is not over it.
   */
  function dropOnCard(g: Gesture, client: Point): IncomingImage | null {
    const card = expandedRef.current
    if (g.kind !== 'image' || !card?.hits(client)) return null
    const image = board.images.find((i) => i.id === g.id)
    if (!image) return null
    const at = card.toCard(client)
    return { image, at: { x: at.x - (g.start.x - image.x), y: at.y - (g.start.y - image.y) } }
  }

  /** Where a board drawing being dragged would land on the open card's canvas, keeping its grab point under the pointer. */
  function drawingOnCard(g: Gesture, client: Point): IncomingDrawing | null {
    const card = expandedRef.current
    if (g.kind !== 'drawing' || !card?.hits(client)) return null
    const drawing = board.drawings.find((d) => d.id === g.id)
    if (!drawing) return null
    const at = card.toCard(client)
    return { drawing, at: { x: at.x - (g.start.x - drawing.x), y: at.y - (g.start.y - drawing.y) } }
  }

  function expand(id: string) {
    setTitleEdit(null)
    setQuickEdit(null)
    setDeletedPreview(null)
    setExpanded({ id, fresh: false })
  }

  /** Closes the open card; while searching, back to the results. */
  function closeExpanded() {
    setExpanded(null)
    if (!search) return
    setSearch({ ...search, listOpen: true })
    focusSearch.current = 'end'
  }

  /** Opens the search with its words selected. An open card stays open; picking a result swaps it for that one. */
  function openSearch() {
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
    const i = results.findIndex((r) => r.card.id === current)
    const next = results.length > 0 ? results[(i + direction + results.length) % results.length].card.id : null
    setSearch({ ...search, current: next, listOpen: true })
    revealPending.current = true
  }

  /** Opens a result as a card opens anywhere. The search keeps its words, so Escape comes back to the same results. */
  function openResult(id: string) {
    // The opened card takes focus from the search field, so Escape and typing go to it.
    setSearch((s) => s && { ...s, current: id, listOpen: false })
    expand(id)
  }

  /**
   * Scrolls the board so a card is on screen and not under the search, its results or another floating control. When
   * it has to move, it lands in the middle of the space below the search.
   */
  function reveal(id: string) {
    const el = scrollRef.current
    const card = board.cards.find((n) => n.id === id)
    if (!el || !card) return
    const frame = el.getBoundingClientRect()
    const target = revealTarget.current && revealTarget.current.until > performance.now() ? revealTarget.current : null
    const from = target ?? { left: el.scrollLeft, top: el.scrollTop }
    const at = cardPoint(card)
    const left = at.x * zoom + origin.x - from.left
    const top = at.y * zoom + origin.y - from.top
    const box: Box = { left, top, right: left + CARD_WIDTH * zoom, bottom: top + CARD_HEIGHT * zoom }
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
    const covered = covers.some((r) => r && box.left < r.right && box.right > r.left && box.top < r.bottom && box.bottom > r.top)
    if (!covered && box.left >= 0 && box.top >= 0 && box.right <= el.clientWidth && box.bottom <= el.clientHeight) return
    const area: Box = { left: rail?.right ?? 16, top: stack?.bottom ?? 16, right: el.clientWidth - 16, bottom: el.clientHeight - 16 }
    const x = (area.left + area.right) / 2
    const y = Math.max(area.top + (CARD_HEIGHT * zoom) / 2, (area.top + area.bottom) / 2)
    const to = {
      left: Math.min(Math.max(0, (at.x + CARD_WIDTH / 2) * zoom + origin.x - x), el.scrollWidth - el.clientWidth),
      top: Math.min(Math.max(0, (at.y + CARD_HEIGHT / 2) * zoom + origin.y - y), el.scrollHeight - el.clientHeight),
    }
    revealTarget.current = { ...to, until: performance.now() + 600 }
    el.scrollTo({ ...to, behavior: reducedMotion() ? 'auto' : 'smooth' })
  }

  function beginGesture(e: PointerEvent, target: GestureTarget) {
    setQuickEdit(null)
    const point = toCanvas(e)
    canvasRef.current!.setPointerCapture(e.pointerId)
    shaken.current = shakeDetector()
    setGesture({ ...target, start: point, current: point, home: homeOf(target) ?? undefined })
  }

  /** The area a card or group being dragged takes up as the drag begins: released back over it, nothing changes. */
  function homeOf(target: GestureTarget): Rect | null {
    const items = target.kind === 'card' ? [{ kind: 'card' as const, id: target.id }] : target.kind === 'group' ? target.members : []
    const rects = items.flatMap((m) => liveRect(m.kind, m.id) ?? [])
    if (rects.length === 0) return null
    const x = Math.min(...rects.map((r) => r.x))
    const y = Math.min(...rects.map((r) => r.y))
    return {
      x,
      y,
      width: Math.max(...rects.map((r) => r.x + r.width)) - x,
      height: Math.max(...rects.map((r) => r.y + r.height)) - y,
    }
  }

  /**
   * Whether releasing a card or group being dragged calls it off: the pointer is back over where it started after
   * leaving it, and not over the trash, the open deleted items or the open card, which lie above the board.
   */
  function cancelsAt(g: Gesture, client: Point): boolean {
    if (!g.home || !g.away || !contains(g.home, g.current)) return false
    return !onTrash(client) && !(deletedOpen && deletedRef.current?.contains(client)) && !expandedRef.current?.contains(client)
  }

  /**
   * A press on an item: selects it and starts dragging it (`handle`: resizing from that corner or edge). One of several
   * selected drags them all; with Shift or Ctrl/Cmd it joins or leaves the selection instead. With Space held, the
   * press goes on to the canvas, which pans.
   */
  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: ItemGesture, id: string, { handle }: { handle?: ResizeHandle } = {}) {
    if (e.button !== 0 || tool !== 'select' || spaceHeld) return
    cardPresses.current = [cardPresses.current[1], kind === 'card' ? id : null]
    e.stopPropagation()
    e.preventDefault()
    // preventDefault keeps focus where it is, so let a title being edited go (it saves on blur).
    if (titleEdit && document.activeElement instanceof HTMLElement) document.activeElement.blur()
    // A press on a search result makes it the one being looked at.
    if (kind === 'card' && search && results.some((r) => r.card.id === id)) setSearch({ ...search, current: id, listOpen: false })
    if (readOnly) {
      // Cards can still be selected (and opened with Enter); nothing moves and no panels open.
      if (kind === 'card') {
        e.currentTarget.focus({ preventScroll: true })
        setSelection({ kind: 'card', id })
      }
      return
    }
    if (kind === 'card' || kind === 'image') e.currentTarget.focus({ preventScroll: true })
    // A drawing has nothing to focus: let go of whatever had it, so Delete reaches the board.
    if (kind === 'drawing' && document.activeElement instanceof HTMLElement) document.activeElement.blur()
    const onTitle = e.target instanceof Element && e.target.closest('.card-title') !== null
    titlePress.current = kind === 'card' && onTitle ? id : null
    const item: Selection = { kind: kind === 'card' || kind === 'drawing' ? kind : kind === 'image' || kind === 'image-resize' ? 'image' : 'bucket', id }
    if (item.kind === 'bucket') toFront(id)
    const resize = kind === 'resize' || kind === 'image-resize'
    if (!resize && adds(e)) {
      titlePress.current = null
      setSelected(togglePicked(selected, item))
      return
    }
    if (!resize && selected.length > 1 && isSelected(item.kind, id)) {
      // What is in a bucket that moves along goes with it, so it doesn't move itself.
      const moving = new Set(selected.filter((s) => s.kind === 'bucket').map((s) => s.id))
      const members = selected.filter((s) => s.kind === 'bucket' || !moving.has(bucketOf(s) ?? ''))
      beginGesture(e, { kind: 'group', id, held: item.kind, members })
      return
    }
    setSelection(item)
    beginGesture(e, { kind, id, handle })
  }

  /** A press inside a bucket, away from its rim: a selection rectangle for the bucket's own items. */
  function onBucketPointerDown(e: PointerEvent<HTMLElement>, bucket: BucketDetail) {
    if (e.target !== e.currentTarget || e.button !== 0 || tool !== 'select' || spaceHeld || readOnly) return
    e.stopPropagation()
    e.preventDefault()
    toFront(bucket.id)
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur()
    cardPresses.current = [cardPresses.current[1], null]
    beginGesture(e, { kind: 'select', bucketId: bucket.id, base: adds(e) ? selected : [] })
  }

  function onCanvasPointerDown(e: PointerEvent) {
    cardPresses.current = [cardPresses.current[1], null]
    // The middle button pans, and so does the left one with Space held (the wheel zooms, Shift + wheel scrolls).
    if (e.button === 1 || (e.button === 0 && spaceHeld)) {
      e.preventDefault()
      const el = scrollRef.current!
      canvasRef.current!.setPointerCapture(e.pointerId)
      setPan({ x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop })
      return
    }
    if (e.button !== 0) return
    if (tool === 'pen') {
      if (readOnly) return
      e.preventDefault()
      setQuickEdit(null)
      setSelection(null)
      canvasRef.current!.setPointerCapture(e.pointerId)
      pen.start(e, ink, strokeWidth)
    } else if (tool === 'bucket') {
      e.preventDefault()
      beginGesture(e, { kind: 'draw' })
    } else if (tool === 'card') {
      // A click with the card tool puts the new card's top-left corner where you clicked, so you can tell where it goes.
      stickCard(snapPoint(toCanvas(e)))
    } else if (readOnly) {
      setSelection(null)
    } else {
      // A selection rectangle: buckets and loose items. A click without dragging lets go of the selection. No text gets
      // selected on the way, and focus leaves whatever had it, as a press on empty space does.
      e.preventDefault()
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur()
      beginGesture(e, { kind: 'select', bucketId: null, base: adds(e) ? selected : [] })
    }
  }

  /** Where a new card carried from the rail goes: centred on a canvas point (the pointer holds it there), snapped. */
  const cardSpot = (p: Point) => snapPoint({ x: p.x - CARD_WIDTH / 2, y: p.y - CARD_HEIGHT / 2 })

  /** Sticks a new card with its top-left corner at a (snapped) canvas point, selects it and opens it with its title ready to type. */
  function stickCard(at: Point) {
    void settle(
      async () => {
        const card = await api.createCard(board.id, { title: '', color: fill, ...cardPlace(at) })
        setSelection({ kind: 'card', id: card.id })
        setExpanded({ id: card.id, fresh: true })
        setTool('select')
        return card
      },
      (card) => step('Add card', [created(target.card(card.id), snap.card(card))]),
    )
  }

  /** Whether a screen point is over the rail's trash can (the card button while a card is dragged). */
  function onTrash(client: Point) {
    const r = trashRef.current?.getBoundingClientRect()
    return !!r && client.x >= r.left && client.x <= r.right && client.y >= r.top && client.y <= r.bottom
  }

  /** Where a new card dragged from the rail would go, in canvas pixels: over the board, not over the open card or the trash. */
  function newCardPoint(client: Point): Point | null {
    if (expandedRef.current?.contains(client) || onTrash(client)) return null
    return boardPoint(client)
  }

  /**
   * A press on the rail's card button. Dragging from there carries a new card, which is stuck where it is released
   * (over a bucket, it lands in that bucket). Without a drag it stays a click, which picks the card tool. Escape, or
   * releasing it off the board or back on the button (a trash can by then), drops it.
   */
  function onCardPointerDown(e: PointerEvent<HTMLButtonElement>) {
    if (e.button !== 0 || readOnly) return
    e.preventDefault()
    const start = { x: e.clientX, y: e.clientY }
    let carried = false
    const end = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', end)
      window.removeEventListener('keydown', onKey, true)
      setNewCard(null)
      setOverTrash(false)
    }
    const onMove = (ev: globalThis.PointerEvent) => {
      const current = { x: ev.clientX, y: ev.clientY }
      if (!carried && !isDrag({ start, current })) return
      carried = true
      setQuickEdit(null)
      setNewCard(current)
      setOverTrash(onTrash(current))
    }
    const onUp = (ev: globalThis.PointerEvent) => {
      end()
      if (!carried) return
      // A drag isn't a click: the click that may follow on the button doesn't pick the tool.
      const swallow = (c: MouseEvent) => c.stopPropagation()
      window.addEventListener('click', swallow, { capture: true, once: true })
      setTimeout(() => window.removeEventListener('click', swallow, true))
      const at = newCardPoint({ x: ev.clientX, y: ev.clientY })
      if (at) stickCard(cardSpot(at))
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
    if (tool === 'pen') movePenRing(penRing.current, toCanvas(e), e.pointerType)
    if (pan) {
      const el = scrollRef.current!
      el.scrollLeft = pan.left - (e.clientX - pan.x)
      el.scrollTop = pan.top - (e.clientY - pan.y)
    } else if (pen.drawing()) {
      pen.extend(e)
    } else if (gesture && !gesture.done) {
      const current = toCanvas(e)
      const next = { ...gesture, current, away: gesture.away || (!!gesture.home && !contains(gesture.home, current)) }
      if (next.kind === 'select') {
        setGesture(next)
        if (isDrag(next)) setSelected(mergePicked(next.base, inRectangle(spanRect(next.start, next.current), next.bucketId)))
        return
      }
      if (next.kind === 'group') {
        // Cards shaken together line up in a grid around the one held, which then moves with the pointer.
        const shake = !next.grid && shaken.current(e.clientX, e.clientY, e.timeStamp)
        const moved = shake ? { ...next, grid: gridFor(next.id, next.members) } : next
        setGesture(moved)
        // Over the trash or the open deleted items they would be deleted; cards over a collapsed bucket go in, over
        // another card (or the open one) they link there; images and drawings over a card are attached to it.
        // Back over where they started, they would stay as they were.
        const client = { x: e.clientX, y: e.clientY }
        const dragged = isDrag(moved)
        const home = cancelsAt(moved, client)
        const trash = dragged && onTrash(client)
        const intoDeleted = dragged && !trash && deletedOpen && !!deletedRef.current?.contains(client)
        const cardsOnly = moved.members.every((m) => m.kind === 'card')
        const fold =
          dragged && cardsOnly && !home && !trash && !intoDeleted && !expandedRef.current?.contains(client) ? foldAt(moved.current) : undefined
        const free = dragged && !home && !trash && !intoDeleted && !fold
        setHomeDrop(home)
        const link = free ? linkDropFor(moved, client) : null
        // Cards over a bucket they aren't all in go into its free spots (over the open card, they stay on the board).
        const arrange = free && !link && !expandedRef.current?.contains(client) ? arrangeFor(moved) : null
        setOverTrash(trash)
        setDeletedDrop(intoDeleted ? client : null)
        setFoldDrop(fold?.id ?? null)
        setLinkDrop(link)
        setAttachDrop(free ? attachDropFor(moved, client) : null)
        setArrangeDrop(arrange)
        return
      }
      const client = { x: e.clientX, y: e.clientY }
      const home = cancelsAt(next, client)
      const trash = next.kind === 'card' && isDrag(next) && onTrash(client)
      setGesture(next)
      // Over the open deleted items a card is deleted too, and over a collapsed bucket it goes in, rather than being
      // linked; back over where it started, it would stay put.
      const intoDeleted = !trash && next.kind === 'card' && isDrag(next) && deletedOpen && !!deletedRef.current?.contains(client)
      const fold =
        next.kind === 'card' && isDrag(next) && !home && !trash && !intoDeleted && !expandedRef.current?.contains(client)
          ? foldAt(next.current)
          : undefined
      setHomeDrop(home)
      setOverTrash(trash)
      setDeletedDrop(intoDeleted ? client : null)
      setFoldDrop(fold?.id ?? null)
      setIncoming(isDrag(next) && !trash && !intoDeleted ? dropOnCard(next, client) : null)
      setIncomingDrawing(isDrag(next) ? drawingOnCard(next, client) : null)
      setLinkDrop(isDrag(next) && !home && !trash && !intoDeleted && !fold ? linkDropFor(next, client) : null)
    }
  }

  /**
   * Drops several selected items dragged together. On the trash can or the open deleted items they are deleted, as
   * Delete does. Cards go into a collapsed bucket, or link from another card (or the open one) to each of them, and
   * stay where they were; images and drawings over a card are attached to it. Anywhere else they move.
   */
  function dropGroup(g: Extract<Gesture, { kind: 'group' }>, client: Point) {
    const intoDeleted = deletedDrop !== null
    const fold = foldDrop ? board.buckets.find((b) => b.id === foldDrop) : undefined
    const arrange = arrangeDrop
    setOverTrash(false)
    setDeletedDrop(null)
    setFoldDrop(null)
    setLinkDrop(null)
    setAttachDrop(null)
    setHomeDrop(false)
    if (cancelsAt(g, client)) {
      // Back over where they started: called off, everything as it was.
      setArrangeDrop(null)
      setGesture(null)
      return
    }
    if (onTrash(client) || intoDeleted) {
      setArrangeDrop(null)
      // Gone at once rather than back in their places; the toast's Undo puts them all back.
      if (intoDeleted) deletedRef.current?.pop()
      setGesture({ ...g, done: true })
      setBinned(selected.map((s) => s.id))
      void deleteSelected().then(() => setBinned([]))
      return
    }
    const cards = g.members
      .flatMap((m) => (m.kind === 'card' ? board.cards.filter((n) => n.id === m.id) : []))
      .toSorted((a, b) => a.y - b.y || a.x - b.x)
    if (fold && cards.length === g.members.length) {
      // Into a collapsed bucket, out of sight with the others, each in a free spot the server finds.
      setGesture({ ...g, done: true })
      setSelected([])
      void putCards(fold, cards, 'Down', []).then((ok) => ok && pop(canvasRef.current?.querySelector(`[data-fold="${fold.id}"]`), 1.08))
      return
    }
    if (arrange) {
      // Into a bucket they aren't all in: the cards take its free spots (it grows towards the side asked when they don't
      // fit, over its neighbours too, so the server puts it on top); images and drawings dropped with them land where they are dropped.
      setGesture({ ...g, done: true })
      toFront(arrange.bucket.id, false)
      const ordered = cards.toSorted((a, b) => arrange.spots[a.id].y - arrange.spots[b.id].y || arrange.spots[a.id].x - arrange.spots[b.id].x)
      void putCards(arrange.bucket, ordered, arrange.side, g.members.filter((m) => m.kind === 'image' || m.kind === 'drawing'))
      return
    }
    const link = linkDropFor(g, client)
    if (link) {
      // The cards dragged go back to their places; the card they were dropped on links to each of them.
      setGesture(null)
      let added = 0
      void runRec(
        () => linkCards(link, cards),
        (result) => {
          added = result.added
          return result.entry
        },
      ).then((ok) => {
        if (!ok) return
        if (added === 0) return notify('Already linked')
        for (const card of cards) pop(cardElement(card.id))
        if (link.window) expandedRef.current?.pop()
        else pop(cardElement(link.card.id))
        notify(added === 1 ? 'Link created' : `${added} links created`)
      })
      return
    }
    const attach = attachDropFor(g, client)
    if (attach) {
      attachGroup(g, attach, client)
      return
    }
    setGesture({ ...g, done: true })
    moveGroup(g.members, g.grid ? 'Line up cards' : undefined)
  }

  /**
   * Attaches several images and drawings dragged together to a card. On the open card they land where the drag shows
   * them; on a card on the board the images are sucked in and the server finds each a free spot, and the drawings keep
   * how they lie together, from the canvas's top-left.
   */
  function attachGroup(g: Extract<Gesture, { kind: 'group' }>, { card, window: onWindow }: AttachDrop, client: Point) {
    const groupImages = g.members.flatMap((m) => (m.kind === 'image' ? board.images.filter((i) => i.id === m.id) : []))
    const groupDrawings = g.members.flatMap((m) => (m.kind === 'drawing' ? board.drawings.filter((d) => d.id === m.id) : []))
    const rects = new Map<string, Rect>([
      ...groupImages.map((i) => [i.id, imageRect(i)] as const),
      ...groupDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    const at = onWindow ? expandedRef.current!.toCard(client) : null
    const drawn = groupDrawings.map((d) => rects.get(d.id)!)
    const left = Math.min(...drawn.map((r) => r.x))
    const top = Math.min(...drawn.map((r) => r.y))
    const spot = (id: string) => {
      const r = rects.get(id)!
      const p = at ? { x: at.x + r.x - g.current.x, y: at.y + r.y - g.current.y } : { x: GRID + r.x - left, y: GRID + r.y - top }
      return onCardCanvas(p, r)
    }
    const animations = onWindow ? [] : groupImages.map((i) => suckInto(i.id, card))
    const n = groupImages.length + groupDrawings.length
    setGesture({ ...g, done: true })
    setSelected([])
    void runAll(`Attach ${count(n, 'item', 'items')}`, undefined, async (changes) => {
      await Promise.all(animations.map((a) => a?.finished.catch(() => {})))
      // One after another: on a card on the board each image's free spot is next to the ones before.
      for (const image of groupImages) {
        const after = await api.updateImage(image.id, onWindow ? { cardId: card.id, ...spot(image.id) } : { cardId: card.id })
        changes.push(patched(target.image(image.id), snap.image(image), snap.image(after), ['cardId', 'x', 'y']))
      }
      for (const drawing of groupDrawings) {
        const after = await api.updateDrawing(drawing.id, { cardId: card.id, ...spot(drawing.id) })
        changes.push(patched(target.drawing(drawing.id), snap.drawing(drawing), snap.drawing(after), ['cardId', 'x', 'y']))
      }
    }).then((ok) => {
      if (!ok) return animations.forEach((a) => a?.cancel())
      if (onWindow) expandedRef.current?.pop()
      notify(`${count(n, 'item', 'items')} embedded`)
    })
  }

  function onCanvasPointerUp(e: PointerEvent) {
    if (pan) {
      setPan(null)
      return
    }
    if (pen.drawing()) {
      pen.finish(runRec, board.id)
      return
    }
    if (!gesture || gesture.done) return
    if (gesture.kind === 'select') {
      setGesture(null)
      // A click without dragging: inside a bucket it selects the bucket (or, adding, it joins or leaves the selection);
      // on the board it leaves only what was added to.
      if (!isDrag(gesture)) setSelected(gesture.bucketId ? togglePicked(gesture.base, { kind: 'bucket', id: gesture.bucketId }) : gesture.base)
      return
    }
    if (gesture.kind === 'group') {
      if (!isDrag(gesture)) {
        // A click on one of several selected selects just that one (a click on a card's title edits it, as ever).
        setGesture(null)
        setSelection({ kind: gesture.held, id: gesture.id })
        if (gesture.held === 'card' && titlePress.current === gesture.id) setTitleEdit({ id: gesture.id, caret: caretAt(e.clientX, e.clientY) })
        return
      }
      dropGroup(gesture, { x: e.clientX, y: e.clientY })
      return
    }
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
      // A click on a card's title edits it, with the text cursor where the click was.
      const id = titlePress.current
      if (gesture.kind === 'card' && id === gesture.id) setTitleEdit({ id, caret: caretAt(e.clientX, e.clientY) })
      return
    }

    const client = { x: e.clientX, y: e.clientY }
    const intoDeleted = deletedDrop !== null
    const fold = gesture.kind === 'card' && foldDrop ? board.buckets.find((b) => b.id === foldDrop) : undefined
    setOverTrash(false)
    setDeletedDrop(null)
    setFoldDrop(null)
    setHomeDrop(false)
    if (cancelsAt(gesture, client)) {
      // Back over where it started: called off, the card stays as it was.
      setLinkDrop(null)
      setGesture(null)
      return
    }
    if (gesture.kind === 'card' && (onTrash(client) || intoDeleted)) {
      // Dropped on the trash can, or on the open deleted items: deleted (the toast and Ctrl/Cmd+Z undo it), and gone at
      // once rather than back in its place.
      if (intoDeleted) deletedRef.current?.pop()
      setLinkDrop(null)
      setIncoming(null)
      setIncomingDrawing(null)
      setGesture({ ...gesture, done: true })
      setBinned([gesture.id])
      void deleteCard(gesture.id).then(() => setBinned([]))
      return
    }
    if (fold) {
      // Dropped on a collapsed bucket: it goes in, out of sight with the others, in a free spot as the server finds one.
      setGesture({ ...gesture, done: true })
      setLinkDrop(null)
      const card = board.cards.find((n) => n.id === gesture.id)!
      void settle(...ops.moveCardInto(card, fold.id)).then((ok) => ok && pop(canvasRef.current?.querySelector(`[data-fold="${fold.id}"]`), 1.08))
      return
    }
    const link = linkDropFor(gesture, client)
    setLinkDrop(null)
    if (link) {
      // Dropped on another card (or the open one's window): that one links to it, and the dragged one stays put.
      setGesture(null)
      const linked = board.cards.find((n) => n.id === gesture.id)!
      let added = false
      void runRec(
        () => linkCards(link, [linked]),
        (result) => {
          added = result.added > 0
          return result.entry
        },
      ).then((ok) => {
        if (!ok) return
        if (!added) return notify('Already linked')
        // Both cards pop: the one dragged (back in its place) and the one that got the link.
        pop(cardElement(linked.id))
        if (link.window) expandedRef.current?.pop()
        else pop(cardElement(link.card.id))
        notify('Link created')
      })
      return
    }

    setGesture({ ...gesture, done: true })
    const { id } = gesture
    const onCard = expanded && dropOnCard(gesture, client)
    const drawingInto = expanded && drawingOnCard(gesture, client)
    setIncoming(null)
    setIncomingDrawing(null)
    const overWindow = !!expandedRef.current?.contains(client)
    const intoCard = gesture.kind === 'image' && !onCard && !overWindow ? cardAt(toCanvas(e)) : undefined
    /** The undo step for an image that moved, was resized or went into a card. */
    const imageStep = (label: string, before: ImageDetail | undefined, keys: string[]) => (after: ImageDetail) =>
      before ? step(label, [patched(target.image(id), snap.image(before), snap.image(after), keys)]) : null
    if (expanded && drawingInto) {
      // Dropped on the open card: the drawing moves onto its canvas (over cards on the board it just moves).
      setSelection(null)
      const { drawing, at } = drawingInto
      void settle(
        () => api.updateDrawing(id, { cardId: expanded.id, ...onCardCanvas(at, drawing) }),
        (after) => step('Put drawing on card', [patched(target.drawing(id), snap.drawing(drawing), snap.drawing(after), ['cardId', 'x', 'y'])]),
      )
    } else if (expanded && onCard) {
      // Dropped on the open card: the image moves onto its canvas.
      setSelection(null)
      void settle(
        () => api.updateImage(id, { cardId: expanded.id, ...onCardCanvas(onCard.at, onCard.image) }),
        imageStep('Embed image', onCard.image, ['cardId', 'x', 'y']),
      ).then((ok) => ok && notify('Image embedded'))
    } else if (intoCard) {
      // Dropped on a card on the board: sucked into it and attached, in a free spot on its canvas, without opening it.
      // The request waits for the animation, so the reload doesn't take the image away halfway.
      setSelection(null)
      const animation = suckInto(id, intoCard)
      void settle(
        async () => {
          await animation?.finished.catch(() => {})
          return api.updateImage(id, { cardId: intoCard.id })
        },
        imageStep('Embed image', board.images.find((i) => i.id === id), ['cardId', 'x', 'y']),
      ).then((ok) => (ok ? notify('Image embedded') : animation?.cancel()))
    } else if (gesture.kind === 'card') {
      const card = board.cards.find((n) => n.id === id)!
      void settle(
        () => api.moveCard(id, cardPlace(cardPoint(card))),
        (moved) => step('Move card', [patched(target.card(id), snap.card(card), snap.card(moved), ['x', 'y'])]),
      )
    } else if (gesture.kind === 'image' || gesture.kind === 'image-resize') {
      const image = board.images.find((i) => i.id === id)!
      const { x, y, width } = imageRect(image)
      const moving = gesture.kind === 'image'
      void settle(
        () => api.updateImage(id, moving ? { x, y } : { x, y, width }),
        imageStep(moving ? 'Move image' : 'Resize image', image, ['x', 'y', 'width']),
      )
    } else if (gesture.kind === 'drawing') {
      const drawing = board.drawings.find((d) => d.id === id)!
      const { x, y } = drawingRect(drawing)
      void settle(
        () => api.updateDrawing(id, { x, y }),
        (after) => step('Move drawing', [patched(target.drawing(id), snap.drawing(drawing), snap.drawing(after), ['x', 'y'])]),
      )
    } else if (gesture.kind === 'bucket') {
      // A moved bucket carries its cards, images and drawings along; undoing the move takes exactly those back.
      const bucket = board.buckets.find((b) => b.id === id)!
      const { x, y } = bucketRect(bucket)
      void settle(
        () => api.updateBucket(id, { x, y }),
        (moved) => step('Move bucket', bucketMove(bucket, moved)),
      )
    } else {
      // With a new size, a new place is a resize from a top or left corner or edge: the server leaves the cards where they are.
      const bucket = board.buckets.find((b) => b.id === id)!
      const { x, y, width, height } = bucketRect(bucket)
      void settle(
        () => api.updateBucket(id, { x, y, width, height }),
        (resized) => step('Resize bucket', [patched(target.bucket(id), snap.bucket(bucket), snap.bucket(resized), ['x', 'y', 'width', 'height'])]),
      )
    }
  }

  // What was dropped on the trash is gone at once.
  // Drawn as they stack, so where buckets overlap the one on top covers the others.
  const buckets = stacked.filter((b) => !binned.includes(b.id)).map((bucket) => ({ bucket, rect: bucketRect(bucket) }))
  // Images in a collapsed bucket fold away with its cards.
  const images = board.images
    .filter((i) => i.cardId === null && !(i.bucketId && collapsedIds.has(i.bucketId)) && !binned.includes(i.id))
    .map((image) => ({ image, rect: imageRect(image) }))
  // Drawings in a collapsed bucket fold away too.
  const drawings = board.drawings
    .filter((d) => d.cardId === null && !(d.bucketId && collapsedIds.has(d.bucketId)) && !binned.includes(d.id))
    .map((drawing) => ({ drawing, rect: drawingRect(drawing) }))
  /** How each tag shows: its saved color, or one being saved or previewed. */
  const tagColors = tagColorsOf(board.tags, tagSaving, tagPreview)
  /** How many images are attached to each card. */
  const attached = new Map<string, number>()
  for (const image of board.images) if (image.cardId) attached.set(image.cardId, (attached.get(image.cardId) ?? 0) + 1)
  // The front bucket's cards above the rest, most recently updated on top, the card being dragged above everything.
  const cards = visibleCards
    .filter((n) => !binned.includes(n.id))
    .toSorted(drawOrder)
    .map((card) => ({ card, point: cardPoint(card) }))
  /** Where a card dragged out of the trash window would land. */
  const outgoingPlace = outgoing ? outgoingSpot(outgoing) : null
  const resizing = gesture && (gesture.kind === 'resize' || gesture.kind === 'image-resize') && !gesture.done ? gesture : null
  const draggedCard = dragging?.kind === 'card' ? cards.find((n) => n.card.id === dragging.id) : undefined
  /** Several selected items being dragged together (the trash can shows for them too). */
  const draggedGroup = dragging?.kind === 'group' && !dragging.done
  /** Around several selected cards, for the toolbar above them; null with fewer than two. */
  const selectedCards = cards.filter(({ card }) => isSelected('card', card.id))
  const selectedCardsRect =
    selectedCards.length >= 2
      ? spanRect(
          { x: Math.min(...selectedCards.map((n) => n.point.x)), y: Math.min(...selectedCards.map((n) => n.point.y)) },
          { x: Math.max(...selectedCards.map((n) => n.point.x + CARD_WIDTH)), y: Math.max(...selectedCards.map((n) => n.point.y + CARD_HEIGHT)) },
        )
      : null
  /** The card a dragged image would be attached to (not while it is over the open card's window). */
  const imageTarget = dragging?.kind === 'image' && !dragging.done && !incoming ? cardAt(dragging.current) : undefined
  /** Where a new card carried from the rail would land, on the canvas. */
  const newCardAt = newCard && newCardPoint(newCard)
  const newCardSpot = newCardAt ? cardSpot(newCardAt) : null
  const dropTarget =
    draggedCard && !linkDrop && !overTrash && !deletedDrop && !foldDrop
      ? bucketUnderCard(stacked, draggedCard.point, draggedCard.card.bucketId, front)
      : newCardSpot
        ? bucketUnderCard(stacked, newCardSpot, null, front)
        : undefined
  const draft =
    gesture?.kind === 'draw' && !gesture.done ? spanRect(snapPoint(gesture.start), snapPoint(gesture.current)) : null
  /** The selection rectangle being dragged out. */
  const marquee = gesture?.kind === 'select' && isDrag(gesture) ? spanRect(gesture.start, gesture.current) : null

  // The canvas reaches from the leftmost and topmost item (or 0,0) to past the furthest one; the margin around it fills
  // the rest of the viewport. Items left of or above 0,0 grow it that way, while they are dragged there too.
  const low = {
    x: Math.min(0, ...buckets.map((b) => b.rect.x), ...cards.map((n) => n.point.x), ...images.map((i) => i.rect.x), ...drawings.map((d) => d.rect.x)),
    y: Math.min(0, ...buckets.map((b) => b.rect.y), ...cards.map((n) => n.point.y), ...images.map((i) => i.rect.y), ...drawings.map((d) => d.rect.y)),
  }
  /** Where canvas point 0,0 is in the scrolled area. */
  const origin = { x: corner.x - low.x * zoom, y: corner.y - low.y * zoom }
  const extent = {
    width:
      CANVAS_MARGIN +
      Math.max(
        0,
        ...buckets.map((b) => b.rect.x + b.rect.width),
        ...cards.map((n) => n.point.x + CARD_WIDTH),
        ...images.map((i) => i.rect.x + i.rect.width),
        ...drawings.map((d) => d.rect.x + d.rect.width),
      ),
    height:
      CANVAS_MARGIN +
      Math.max(
        0,
        ...buckets.map((b) => b.rect.y + b.rect.height),
        ...cards.map((n) => n.point.y + CARD_HEIGHT),
        ...images.map((i) => i.rect.y + i.rect.height),
        ...drawings.map((d) => d.rect.y + d.rect.height),
      ),
  }

  // When the canvas grows or shrinks on the left or top, the scroll position moves by as much, so what is on screen
  // (a card being dragged there too) stays where it is.
  const lastLow = useRef(low)
  useLayoutEffect(() => {
    const el = scrollRef.current!
    el.scrollLeft += (lastLow.current.x - low.x) * zoom
    el.scrollTop += (lastLow.current.y - low.y) * zoom
    lastLow.current = { x: low.x, y: low.y }
  }, [low.x, low.y, zoom])

  const selectedBucket = selection?.kind === 'bucket' ? board.buckets.find((b) => b.id === selection.id) : undefined
  const selectedImage = selection?.kind === 'image' ? board.images.find((i) => i.id === selection.id) : undefined
  const selectedDrawing = selection?.kind === 'drawing' ? drawings.find((d) => d.drawing.id === selection.id) : undefined
  const expandedCard = expanded && board.cards.find((n) => n.id === expanded.id)
  const countIn = (bucketId: string) => board.cards.filter((n) => n.bucketId === bucketId).length
  const imagesIn = (bucketId: string) => board.images.filter((i) => i.bucketId === bucketId && !i.cardId).length
  // Filter on tags that still exist (one may have been renamed or deleted meanwhile); any of them matches.
  const filter = tagFilter.filter((t) => board.tags.some((bt) => bt.name === t))
  const tagFaded = (card: CardDetail) => filter.length > 0 && !card.tags.some((t) => filter.includes(t))
  // The search fades the cards without its words too. Its results are the matches the tag filter lets through, in
  // board order (bucket by bucket, loose cards last).
  const terms = useMemo(() => searchTerms(search?.query ?? ''), [search?.query])
  const matches = useMemo(() => {
    const found = new Map<string, SearchField[]>()
    if (terms.length === 0) return found
    for (const card of visibleCards) {
      const fields = matchCard(card, terms)
      if (fields) found.set(card.id, fields)
    }
    return found
  }, [visibleCards, terms])
  const dimmed = (card: CardDetail) => tagFaded(card) || (terms.length > 0 && !matches.has(card.id))
  const results: SearchResult[] = board.cards
    .filter((n) => matches.has(n.id) && !tagFaded(n))
    .map((card) => ({ card, fields: matches.get(card.id)! }))
  /** The result being looked at: the one picked, or else the first. */
  const current = results.find((r) => r.card.id === search?.current)?.card.id ?? results[0]?.card.id ?? null

  useLayoutEffect(() => {
    if (!revealPending.current) return
    revealPending.current = false
    const id = revealId.current ?? current
    revealId.current = null
    if (id) reveal(id)
  })
  const quickCard = quickEdit && board.cards.find((n) => n.id === quickEdit.cardId)
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
            className={`canvas tool-${tool}${pan ? ' panning' : spaceHeld ? ' space-pan' : ''}`}
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
              const [first, second] = cardPresses.current
              if (second && first === second) expand(second)
            }}
            onPointerMove={onCanvasPointerMove}
            onPointerLeave={() => movePenRing(penRing.current, null)}
            onPointerUp={onCanvasPointerUp}
            onPointerCancel={() => {
              setGesture(null)
              pen.cancel()
              setPan(null)
              setIncoming(null)
              setIncomingDrawing(null)
              setLinkDrop(null)
              setAttachDrop(null)
              setArrangeDrop(null)
              setFoldDrop(null)
              setHomeDrop(false)
              setDeletedDrop(null)
              setOverTrash(false)
            }}
            // Losing the pointer without a release ends a pan or an unfinished drag, so nothing stays stuck mid-air.
            onLostPointerCapture={() => {
              setPan(null)
              setGesture((g) => (g && !g.done ? null : g))
              pen.cancel()
              setIncoming(null)
              setIncomingDrawing(null)
              setLinkDrop(null)
              setAttachDrop(null)
              setFoldDrop(null)
              setHomeDrop(false)
              setDeletedDrop(null)
              setOverTrash(false)
            }}
          >
            {buckets.map(({ bucket, rect }) => {
              const count = countIn(bucket.id)
              const countLabel = count === 1 ? '1 card' : `${count} cards`
              const images = imagesIn(bucket.id)
              const imagesLabel = images === 1 ? '1 image' : `${images} images`
              const contents = `${countLabel}${images > 0 ? `, ${imagesLabel}` : ''}`
              // How many cards and images are in it, each with its icon: a zero is left out, but an empty bucket shows 0 cards.
              const counts = (className: string) => (
                <>
                  {(count > 0 || images === 0) && (
                    <span className={className} title={countLabel}>
                      <CardIcon size={12} />
                      {count}
                    </span>
                  )}
                  {images > 0 && (
                    <span className={className} title={imagesLabel}>
                      <ImageIcon size={12} />
                      {images}
                    </span>
                  )}
                </>
              )
              if (bucket.collapsed) {
                // Folded up: a square in its top-right corner with its name and what is inside.
                const square = collapsedRect(rect)
                return (
                  <section
                    key={bucket.id}
                    aria-label={`${bucket.name} (collapsed, ${contents})`}
                    data-fold={bucket.id}
                    className={['bucket-collapsed', foldDrop === bucket.id && 'drop-target', isSelected('bucket', bucket.id) && 'selected']
                      .filter(Boolean)
                      .join(' ')}
                    style={{ left: square.x, top: square.y, width: square.width, height: square.height }}
                    title="Drag to move the bucket"
                    onPointerDown={(e) => onItemPointerDown(e, 'bucket', bucket.id)}
                  >
                    <span className={`bucket-collapsed-counts${count > 0 && images > 0 ? ' both' : ''}`} aria-hidden>
                      {counts('bucket-collapsed-count')}
                    </span>
                    <span className="bucket-collapsed-name">{bucket.name}</span>
                    {!readOnly && (
                      <button
                        type="button"
                        className="bucket-fold"
                        aria-label={`Open ${bucket.name}`}
                        title="Open the bucket"
                        onPointerDown={(e) => e.stopPropagation()}
                        onClick={() => toggleCollapsed(bucket)}
                      >
                        <ChevronDownIcon size={13} />
                      </button>
                    )}
                  </section>
                )
              }
              return (
                <section
                  key={bucket.id}
                  aria-label={bucket.name}
                  className={[
                    'bucket',
                    front === bucket.id && 'in-front',
                    (dropTarget?.id === bucket.id || (arrangeDrop?.bucket.id === bucket.id && !dragging?.done)) && 'drop-target',
                    isSelected('bucket', bucket.id) && 'selected',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                  style={{ left: rect.x, top: rect.y, width: rect.width, height: rect.height }}
                  // A drag inside the bucket selects what is in it; a click there selects the bucket. Its name and its rim move it.
                  onPointerDown={(e) => onBucketPointerDown(e, bucket)}
                >
                  {(['n', 'e', 's', 'w'] as const).map((side) => (
                    <span
                      key={side}
                      className={`bucket-rim ${side}`}
                      aria-hidden="true"
                      onPointerDown={(e) => onItemPointerDown(e, 'bucket', bucket.id)}
                    />
                  ))}
                  <button
                    type="button"
                    className="bucket-head"
                    title="Drag to move the bucket and its cards"
                    onPointerDown={(e) => onItemPointerDown(e, 'bucket', bucket.id)}
                    // Enter or Space on the name. A press that added to the selection is a click here too: leave it be.
                    onClick={(e) => {
                      if (adds(e)) return
                      setSelection({ kind: 'bucket', id: bucket.id })
                      toFront(bucket.id)
                    }}
                  >
                    <span className="bucket-name">{bucket.name}</span>
                    <span className="bucket-counts" aria-label={contents}>
                      {counts('count')}
                    </span>
                  </button>
                  {!readOnly && (
                    <button
                      type="button"
                      className="bucket-fold"
                      aria-label={`Collapse ${bucket.name}`}
                      title="Collapse the bucket: its cards fold away with it"
                      onPointerDown={(e) => e.stopPropagation()}
                      onClick={() => toggleCollapsed(bucket)}
                    >
                      <ChevronUpIcon size={13} />
                    </button>
                  )}
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
                selected={isSelected('image', image.id)}
                lifted={lifting(image.id)}
                front={inFront(image)}
                onPointerDown={(e) => onItemPointerDown(e, 'image', image.id)}
                // One of several selected has no handles: they move together, but resize one at a time.
                onResizePointerDown={
                  readOnly || selectedImage?.id !== image.id ? undefined : (e, handle) => onItemPointerDown(e, 'image-resize', image.id, { handle })
                }
                onDelete={readOnly ? undefined : () => deleteImage(image.id)}
              />
            ))}

            {newCardSpot && (
              <div
                className="card-slot"
                aria-hidden="true"
                style={{ left: newCardSpot.x, top: newCardSpot.y, width: CARD_WIDTH, height: CARD_HEIGHT }}
              />
            )}

            {outgoingPlace && (
              <div
                className="card-slot"
                aria-hidden="true"
                style={{ left: outgoingPlace.x, top: outgoingPlace.y, width: CARD_WIDTH, height: CARD_HEIGHT }}
              />
            )}

            {draggedCard && (
              <div
                className="card-slot"
                aria-hidden="true"
                style={{ left: draggedCard.card.x, top: draggedCard.card.y, width: CARD_WIDTH, height: CARD_HEIGHT }}
              />
            )}

            {cards.map(({ card, point }) => (
              <Card
                key={card.id}
                card={card}
                point={point}
                images={attached.get(card.id)}
                tagColors={tagColors}
                selected={isSelected('card', card.id)}
                lifted={lifting(card.id) || remoteMoving(card.id)}
                front={inFront(card)}
                dimmed={dimmed(card)}
                highlight={matches.has(card.id) ? terms : undefined}
                current={card.id === current}
                dropTarget={
                  imageTarget?.id === card.id ||
                  (linkDrop?.window === false && linkDrop.card.id === card.id) ||
                  (attachDrop?.window === false && attachDrop.card.id === card.id)
                }
                onPointerDown={(e) => onItemPointerDown(e, 'card', card.id)}
                onExpand={() => expand(card.id)}
                editingTitle={titleEdit?.id === card.id}
                titleCaret={titleEdit?.id === card.id ? titleEdit.caret : undefined}
                onRenameTitle={readOnly ? undefined : (title) => editCard(card, { title })}
                onTitleDone={() => setTitleEdit(null)}
                onEditTags={readOnly ? undefined : (anchor) => setQuickEdit({ cardId: card.id, kind: 'tags', anchor })}
                onEditAssignees={readOnly ? undefined : (anchor) => setQuickEdit({ cardId: card.id, kind: 'people', anchor })}
              />
            ))}

            {drawings.map(({ drawing, rect }) => (
              <DrawingView
                key={drawing.id}
                drawing={drawing}
                rect={rect}
                selected={isSelected('drawing', drawing.id)}
                lifted={lifting(drawing.id)}
                front={inFront(drawing)}
                onPointerDown={(e) => onItemPointerDown(e as PointerEvent<HTMLElement>, 'drawing', drawing.id)}
                zoom={zoom}
                onReshape={readOnly || selection?.id !== drawing.id ? undefined : (reshape) => settle(...ops.reshapeDrawing(drawing, reshape))}
              />
            ))}

            {pen.strokes.map((s, i) => (
              <StrokeSvg key={i} points={s.points} color={inkHex[s.color]} strokeWidth={s.width} at={{ x: 0, y: 0 }} />
            ))}

            {tool === 'pen' && !readOnly && <PenRing ringRef={penRing} width={strokeWidth} zoom={zoom} />}

            {selectedCardsRect && !readOnly && !gesture && <SelectionToolbar rect={selectedCardsRect} zoom={zoom} onSort={sortCards} />}

            {selectedDrawing && !readOnly && !(dragging?.id === selectedDrawing.drawing.id && !dragging.done) && (
              <DrawingToolbar
                drawing={selectedDrawing.drawing}
                rect={selectedDrawing.rect}
                zoom={zoom}
                onRecolor={(color) => recolorDrawing(selectedDrawing.drawing, color)}
                onDelete={() => deleteDrawing(selectedDrawing.drawing.id)}
              />
            )}

            {remoteDrags
              .filter((r) => !r.endedAt && r.id !== dragging?.id)
              .flatMap((r) =>
                // Several items moving together are outlined each; the name goes on the first.
                remoteRects(r).map((rect, i) => {
                  const style = { left: rect.x, top: rect.y, width: rect.width, height: rect.height, '--who': presenceColor(r.principalId) }
                  return (
                    <div key={`${r.key}:${i}`} className="remote-drag" aria-hidden="true" style={style as CSSProperties}>
                      {i === 0 && <span className="remote-drag-name">{r.displayName}</span>}
                    </div>
                  )
                }),
              )}

            {marquee && (
              <div className="marquee" aria-hidden="true" style={{ left: marquee.x, top: marquee.y, width: marquee.width, height: marquee.height }} />
            )}

            {gesture?.home && gesture.away && !gesture.done && (
              <div
                className={`drag-home${homeDrop ? ' over' : ''}`}
                aria-hidden="true"
                style={{ left: gesture.home.x, top: gesture.home.y, width: gesture.home.width, height: gesture.home.height }}
              />
            )}
          </div>
        </div>
      </div>

      <ToolRail
        tool={tool}
        onToolChange={setTool}
        fill={fill}
        onFillChange={setFill}
        ink={ink}
        onInkChange={setInk}
        strokeWidth={strokeWidth}
        onStrokeWidthChange={setStrokeWidth}
        onCardPointerDown={onCardPointerDown}
        trash={(draggedCard && !dragging?.done) || draggedGroup || newCard ? (overTrash ? 'over' : 'shown') : undefined}
        trashRef={trashRef}
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
        matching={board.cards.filter((n) => !tagFaded(n)).length}
        total={board.cards.length}
      />
      <div className={`board-top${search ? ' searching' : ''}`} ref={boardTop}>
        {search && (
          <BoardSearch
            inputRef={searchInput}
            query={search.query}
            onQueryChange={changeQuery}
            terms={terms}
            results={results}
            current={current}
            listOpen={search.listOpen}
            onListOpenChange={(listOpen) => setSearch((s) => s && { ...s, listOpen })}
            onMove={moveInResults}
            onOpen={openResult}
            onClose={closeSearch}
            faded={board.cards.filter((n) => matches.has(n.id) && tagFaded(n)).length}
            onClearFilter={() => {
              setTagFilter([])
              revealPending.current = true
            }}
            bucketName={(card) => board.buckets.find((b) => b.id === card.bucketId)?.name ?? 'Loose'}
          />
        )}
        {readOnly && <div className="read-only-badge">View only</div>}
        {hint && <div className="canvas-hint">{hint}</div>}
      </div>
      <ViewControls
        undo={readOnly ? undefined : { label: undo.nextUndo, disabled: !!gesture || stroke || cardBusy, onClick: undo.undo }}
        redo={readOnly ? undefined : { label: undo.nextRedo, disabled: !!gesture || stroke || cardBusy, onClick: undo.redo }}
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={{ x: low.x, y: low.y, width: extent.width - low.x, height: extent.height - low.y }}
        viewport={{ x: (view.x - origin.x) / zoom, y: (view.y - origin.y) / zoom, width: view.width / zoom, height: view.height / zoom }}
        buckets={board.buckets}
        cards={visibleCards}
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
          cardCount={countIn(selectedBucket.id)}
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

      {quickCard && quickEdit?.kind === 'tags' && (
        <TagPicker
          key={quickCard.id}
          anchor={quickEdit.anchor}
          tags={quickCard.tags}
          boardTags={board.tags}
          tagColors={tagColors}
          onChange={(tags) => editCard(quickCard, { tags })}
          onRename={renameTag}
          onRecolor={recolorTag}
          onPreview={setTagPreview}
          onDelete={deleteTag}
          onClose={() => setQuickEdit(null)}
        />
      )}
      {quickCard && quickEdit?.kind === 'people' && (
        <AssigneePicker
          key={quickCard.id}
          anchor={quickEdit.anchor}
          assignees={quickCard.assignees}
          people={board.people}
          onChange={(assigneeIds) => editCard(quickCard, { assigneeIds })}
          onClose={() => setQuickEdit(null)}
        />
      )}

      {expandedCard && (
        <ExpandedCard
          key={expandedCard.id}
          card={expandedCard}
          bucket={board.buckets.find((b) => b.id === expandedCard.bucketId)}
          visiting={board.visiting}
          images={board.images.filter((i) => i.cardId === expandedCard.id)}
          cards={board.cards}
          handle={expandedRef}
          run={runRec}
          onBusy={setCardBusy}
          incoming={incoming}
          drawings={board.drawings.filter((d) => d.cardId === expandedCard.id)}
          incomingDrawing={incomingDrawing}
          linking={!!linkDrop?.window || !!attachDrop?.window}
          boardPoint={boardPoint}
          boardZoom={zoom}
          focusTitle={expanded?.fresh ?? false}
          onChange={(changes) => editCard(expandedCard, changes)}
          boardTags={board.tags}
          tagColors={tagColors}
          people={board.people}
          onRenameTag={renameTag}
          onRecolorTag={recolorTag}
          onPreviewTagColor={setTagPreview}
          onDeleteTag={deleteTag}
          onDelete={() => deleteCard(expandedCard.id)}
          onAddImages={(files, at) => void addImages(files, { cardId: expandedCard.id, at })}
          onPickImages={() => void addImagesFromPicker({ cardId: expandedCard.id })}
          onClose={closeExpanded}
          readOnly={readOnly}
          highlight={matches.has(expandedCard.id) ? terms : undefined}
        />
      )}

      {deletedOpen && (
        <TrashWindow
          boardId={board.id}
          reload={board}
          readOnly={readOnly}
          tagColors={tagColors}
          handle={deletedRef}
          dropOver={deletedDrop !== null}
          boardZoom={zoom}
          onClose={() => {
            setDeletedOpen(false)
            setDeletedPreview(null)
          }}
          onOpenCard={(card, images) => {
            // One card window at a time.
            setExpanded(null)
            setDeletedPreview({ card, images })
          }}
          onPutBack={(card) => putBack(card)}
          onPurge={purge}
          onEmpty={emptyTrash}
          onOutgoing={setOutgoing}
          onDropOut={dropOut}
          onBusy={setDeletedBusy}
        />
      )}

      {/* A card in the trash, as it is inside: read only, with its images. */}
      {deletedPreview && !expandedCard && (
        <ExpandedCard
          key={`trash:${deletedPreview.card.id}`}
          card={deletedPreview.card}
          bucket={undefined}
          images={deletedPreview.images}
          cards={board.cards}
          handle={expandedRef}
          run={runRec}
          onBusy={setCardBusy}
          incoming={null}
          linking={false}
          boardPoint={boardPoint}
          boardZoom={zoom}
          focusTitle={false}
          onChange={async () => false}
          boardTags={board.tags}
          tagColors={tagColors}
          people={board.people}
          onRenameTag={async () => false}
          onRecolorTag={async () => false}
          onPreviewTagColor={() => {}}
          onDeleteTag={async () => false}
          onDelete={() => {}}
          onAddImages={() => {}}
          onPickImages={() => {}}
          onClose={() => setDeletedPreview(null)}
          readOnly
          deleted={{ onPutBack: readOnly ? undefined : () => putBack(deletedPreview.card) }}
        />
      )}

      {/* The board draws below the open card and the trash, so a card dragged over their windows is shown on top, at the pointer. */}
      {draggedCard &&
        dragging &&
        (linkDrop?.window || deletedDrop) &&
        createPortal(
          <div
            className="drag-ghost card-ghost"
            aria-hidden="true"
            style={{
              left: (deletedDrop ?? linkDrop!.client).x - (dragging.start.x - draggedCard.card.x) * zoom,
              top: (deletedDrop ?? linkDrop!.client).y - (dragging.start.y - draggedCard.card.y) * zoom,
              transform: `scale(${zoom})`,
            }}
          >
            <Card card={draggedCard.card} point={{ x: 0, y: 0 }} images={attached.get(draggedCard.card.id)} tagColors={tagColors} selected={false} lifted onPointerDown={() => {}} onExpand={() => {}} />
          </div>,
          document.body,
        )}

      <Toast message={toast} onDone={clearToast} />

      {/* A new card carried from the tool rail follows the pointer, at the board's zoom. */}
      {newCard &&
        createPortal(
          <div
            className={`drag-ghost card-ghost${overTrash ? ' over-trash' : ''}`}
            aria-hidden="true"
            style={{
              left: newCard.x - (CARD_WIDTH / 2) * zoom,
              top: newCard.y - (CARD_HEIGHT / 2) * zoom,
              transform: `scale(${zoom})`,
            }}
          >
            <div className={`card lifted fill-${fill.toLowerCase()}`} style={{ width: CARD_WIDTH, height: CARD_HEIGHT }}>
              <div className="card-head">
                <span className="card-title" />
              </div>
            </div>
          </div>,
          document.body,
        )}
    </section>
  )
}
