import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent, type RefObject } from 'react'
import { api } from '../api/client'
import type { BoardDetail, BucketDetail, CardColor, CardDetail, CursorUpdate, DragUpdate, DropBucket, LiveViewer, Point, Rect, ResizeHandle } from '../api/types'
import type { CursorStore, LiveDrag } from '../live/useLive'
import { Toast } from '../components/Toast'
import { BoardFilters } from './BoardFilters'
import { CardGhost, NewCardGhost, RemoteDragOutlines, Slot } from './BoardGhosts'
import { BoardPanels, type QuickEdit } from './BoardPanels'
import { bucketCounts, bucketOf, cardBox, drawOrder, itemsById, NOTHING_IN, type Selection } from './boardItems'
import type { Gesture, GestureTarget, GroupGesture, ItemDrag, ItemGesture } from './boardGesture'
import { BoardSearch } from './BoardSearch'
import { BoardTrash } from './BoardTrash'
import { CardIndex } from './CardIndex'
import { BoardBucket, BoardCard, BoardCollapsedBucket, BoardDrawing, BoardImage, type BoardItemHandlers } from './boardItemViews'
import type { BucketCount } from './BucketView'
import { usePan, useFileDrop } from './canvasHooks'
import { BOARD_PEN, inkColor, movePenRing, usePen } from './drawings'
import { DrawingToolbar, PenRing, StrokeSvg } from './DrawingView'
import { dropTargetsFor, NO_DROPS, released, useDropProbe, type DropTargets } from './dropTargets'
import { ExpandedCard, type ExpandedCardHandle } from './ExpandedCard'
import { isDrag } from './gesture'
import { MAX_COORDINATE, MIN_BUCKET_HEIGHT, MIN_BUCKET_WIDTH, MIN_COORDINATE, backToFront, bucketAtPointer, bucketUnderCard, collapsedRect, intersects, isLooseDrawing, layersBackToFront, snapPoint, spanRect } from './layout'
import { activeDrags, draggingOf, gridFor, itemKindOf, liveGeometry, sentDrag } from './liveGeometry'
import { ops, recordingRun, settling } from './ops'
import { RemoteCursors } from './RemoteCursors'
import { resizeCursor } from './resizeCursor'
import { adds, bounds, contains, mergePicked, togglePicked, touches, useSelection, useSpaceHeld } from './selection'
import { SelectionToolbar } from './SelectionToolbar'
import { headsOf, pinsOf, whereIs, type PinPlace } from './peers'
import { shakeDetector } from './shake'
import { tagColorsOf, type TagColorPreview } from './tagColors'
import { ToolRail } from './ToolRail'
import type { Tool } from './tools'
import type { BoardState } from './useBoard'
import { useBoardClipboard } from './useBoardClipboard'
import { useBoardDrops } from './useBoardDrops'
import { IMAGE_OFFSET, useBoardEdits } from './useBoardEdits'
import { useBoardFilters } from './useBoardFilters'
import { useBoardKeys } from './useBoardKeys'
import { useBoardMoves } from './useBoardMoves'
import { useBoardSearch, useReveal } from './useBoardSearch'
import { useBoardView, useCanvasExtent } from './useBoardView'
import { useCardAddress } from './useCardAddress'
import { useNewCardDrag } from './useNewCardDrag'
import { useStableHandlers } from './useStableHandlers'
import { useStroke } from './useStroke'
import { useToast } from './useToast'
import { describeMissingCard } from './missingCard'
import { useTrashWindow } from './useTrashWindow'
import { useUndo, type UndoHistory } from './useUndo'
import { BoardMapItems, ViewControls, type HistoryButtons } from './ViewControls'
import { useVisibleWindow } from './visibleWindow'
import { FAR_ZOOM, stepZoom } from './zoom'

const hints: Partial<Record<Tool, string>> = {
  card: 'Click anywhere to stick a card',
  bucket: 'Drag across the board to draw a bucket',
  pen: 'Draw anywhere on the board. Esc or V to stop',
}

/** How long a thumbtack picked in the top bar pulses and shows its name. */
const FOCUS_MS = 2400

/**
 * Tells the others where your pointer is on the board (`onCursor`): as it moves, and as the view scrolls or zooms under
 * a pointer that stays put. Gone when the pointer leaves the board, the tab goes to the background, or the board stops
 * showing cursors.
 */
function usePointing({ scrollRef, toCanvas, onCursor }: {
  scrollRef: RefObject<HTMLDivElement | null>
  toCanvas: (e: { clientX: number; clientY: number }) => Point
  onCursor?: (point: CursorUpdate | null) => void
}) {
  /** Where the pointer is over the board, in screen pixels; null when it isn't. */
  const client = useRef<Point | null>(null)
  const latest = useRef({ toCanvas, onCursor })
  useEffect(() => {
    latest.current = { toCanvas, onCursor }
  })
  const send = useCallback(() => {
    const { toCanvas, onCursor } = latest.current
    if (!onCursor || !client.current) return
    const at = toCanvas({ clientX: client.current.x, clientY: client.current.y })
    const clamp = (v: number) => Math.round(Math.min(MAX_COORDINATE, Math.max(MIN_COORDINATE, v)))
    onCursor({ x: clamp(at.x), y: clamp(at.y) })
  }, [])

  useEffect(() => {
    if (!onCursor) return
    const el = scrollRef.current
    const hidden = () => {
      if (document.visibilityState === 'hidden') onCursor(null)
    }
    el?.addEventListener('scroll', send, { passive: true })
    document.addEventListener('visibilitychange', hidden)
    return () => {
      el?.removeEventListener('scroll', send)
      document.removeEventListener('visibilitychange', hidden)
      onCursor(null)
    }
  }, [onCursor, scrollRef, send])

  return {
    point(at: Point) {
      client.current = at
      send()
    },
    leave() {
      client.current = null
      latest.current.onCursor?.(null)
    },
  }
}

/** Where in a card's title text a screen point is, as a character offset; undefined when it isn't on the text. */
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
  /** Other people's pointers and selections (live updates); shown for those in `viewers`. */
  cursors?: CursorStore
  /** The others on the board, whose pointers and pins show; none when the board shows no cursors. */
  viewers?: LiveViewer[]
  /** Where your pointer is on the board, for the others to see; null when it left. Omitted when the board shows no cursors. */
  onCursor?: (point: CursorUpdate | null) => void
  /** What you have selected on the board, for the others to see. Omitted when the board shows no cursors. */
  onSelect?: (items: string[]) => void
  /** Someone picked in the top bar: the view goes to their pointer (or pins). `n` counts the picks, so picking them again goes again. */
  focus?: { principalId: string; n: number } | null
}

/** The board: its buckets, cards, images and drawings on a freeform canvas, with the tool rail and floating windows around it. */
export function BoardCanvas({ board, run, undoHistory, setPaused, remoteDrags = [], onDrag, cursors, viewers = [], onCursor, onSelect, focus }: BoardCanvasProps) {
  /** Viewers can look around and open cards, but not change anything. */
  const readOnly = board.yourRole === 'Viewer'
  const [tool, setTool] = useState<Tool>('select')
  const [fill, setFill] = useState<CardColor>('Butter')
  /** The pen's ink and size, kept for the next visit. */
  const { ink, setInk, width: strokeWidth, setWidth: setStrokeWidth } = usePen(BOARD_PEN)
  /** The ring under the pointer that shows the pen's size. */
  const penRing = useRef<HTMLDivElement>(null)
  const { selected, setSelected, selection, setSelection, unselect, isSelected } = useSelection<Selection>()
  /** What someone else is dragging right now, by itself or with others. */
  const remoteIds = new Set(remoteDrags.flatMap((r) => (r.endedAt ? [] : [r.id, ...(r.items ?? [])])))
  const remoteMoving = (id: string) => remoteIds.has(id)
  /** Space is held: a left press pans, as the middle button does. */
  const spaceHeld = useSpaceHeld()
  /** The tag or people window opened from a card on the board, next to the part that was clicked (screen pixels). */
  const [quickEdit, setQuickEdit] = useState<QuickEdit | null>(null)
  const filters = useBoardFilters(board)
  /** A tag color under the pointer in the palette: it shows on the tag everywhere. */
  const [tagPreview, setTagPreview] = useState<TagColorPreview | null>(null)
  /** The search, hints and view-only badge at the top of the board. */
  const boardTop = useRef<HTMLDivElement>(null)
  /** The card whose title is being edited in place, and where the text cursor starts (default: the end). */
  const [titleEdit, setTitleEdit] = useState<{ id: string; caret?: number } | null>(null)
  /** The card whose title the current press started on: a click there (no drag) edits the title. */
  const titlePress = useRef<string | null>(null)
  /** The cards the last two presses were on (null: something else): the canvas gets the double-click, and expands that card. */
  const cardPresses = useRef<[string | null, string | null]>([null, null])
  const [gesture, setGesture] = useState<Gesture | null>(null)
  /** Whether the drag in progress has become a shake (cards shaken together line up in a grid). */
  const shaken = useRef(shakeDetector())
  /** A drag in progress on the open card's canvas. */
  const [cardBusy, setCardBusy] = useState(false)
  /** What the drag in progress would be dropped on (dropTargetsFor), set as the pointer moves. */
  const [drops, setDrops] = useState<DropTargets>(NO_DROPS)
  const { incoming, incomingDrawing, link: linkDrop, attach: attachDrop, trash: overTrash, deleted: deletedDrop, fold: foldDrop, home: homeDrop } = drops
  /** A group's spots in a bucket; they outlive the drop (see `released`), but only count while the group is dragged. */
  const arrangeDrop = gesture?.kind === 'group' ? drops.arrange : null
  /** The rail's card button, a trash can while something is dragged: dropped there, it is deleted. */
  const trashRef = useRef<HTMLButtonElement>(null)
  /** What was dropped on the trash, hidden until the reload removes it. */
  const [binned, setBinned] = useState<string[]>([])
  const expandedRef = useRef<ExpandedCardHandle>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  const pan = usePan(scrollRef, canvasRef)
  const { toast, clearToast, notify } = useToast()
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
        if (t.kind === 'card' && address.expanded?.id === t.id) address.setExpanded(null)
        unselect(t.id)
      }
    },
  })
  /** Runs a change through the board and records it for undo, as `describe` tells from its result. */
  const runRec = recordingRun(run, undo)
  /** The same, then lets go of the finished gesture. */
  const settle = settling(runRec, () => setGesture(null))
  /** Last pointer position over the canvas, in canvas pixels: where pasted items go. */
  const lastPointer = useRef<Point | null>(null)
  const trash = useTrashWindow({
    board,
    runRec,
    settle,
    notify,
    onPutBack: (card) => {
      setSelection({ kind: 'card', id: card.id })
      // The board scrolls to where it went (out of the window's way) once it has reloaded.
      search.revealSoon(card.id)
    },
    boardPoint: (client) => (expandedRef.current?.contains(client) ? null : boardPoint(client)),
    dropBucket,
  })
  const address = useCardAddress(board, {
    trashOpen: trash.open,
    onFollow: (id, toTrash) => {
      setTitleEdit(null)
      setQuickEdit(null)
      if (id) setSelection({ kind: 'card', id })
      if (toTrash) trash.setOpen(true)
    },
    onMissing: (route) => {
      void describeMissingCard(board, route).then(({ text, deleted }) =>
        notify({
          text,
          tone: 'warn',
          action: deleted && {
            label: 'Show',
            onAction: () => {
              trash.setOpen(true)
              trash.setPreview(deleted)
            },
          },
        }),
      )
    },
    onExpand: () => {
      setTitleEdit(null)
      setQuickEdit(null)
      trash.setPreview(null)
    },
    // While searching, back to the results.
    onClose: () => search.showResults(),
  })
  const { expanded, setExpanded, expand, close: closeExpanded } = address

  /** Buckets as they stack, the same for everyone (`stackedAt`): the last one on top. */
  const stacked = backToFront(board.buckets)
  /**
   * A bucket or loose drawing just opened, pressed or moved here, shown in front at once (for viewers, who can't change
   * the stack, only here). `over`: what was on top when it was picked; once that changes, the server's order decides again.
   */
  const [picked, setPicked] = useState<{ id: string; over: string | undefined } | null>(null)
  const serverLayers = layersBackToFront(board.buckets, board.drawings)
  const top = serverLayers.at(-1)?.id
  const pick = picked?.over === top ? picked?.id : undefined
  /** Raised above the rest here: one picked, or the bucket selected cards are being dragged into, so you see it grow. */
  const raised = arrangeDrop?.bucket.id ?? pick
  /**
   * Buckets and loose drawings as they are drawn, back to front: as they stack, with the one raised here on top. Each
   * bucket is drawn with what is in it, so it covers the ones below and everything in them.
   */
  const layers = raised ? [...serverLayers.filter((l) => l.id !== raised), ...serverLayers.filter((l) => l.id === raised)] : serverLayers
  /** The buckets as they are drawn, back to front. */
  const drawnBuckets = layers.flatMap((l) => (l.kind === 'bucket' ? [l.bucket] : []))
  /** The topmost open bucket as drawn here. */
  const front = drawnBuckets.findLast((b) => !b.collapsed)?.id ?? null
  /**
   * Brings a bucket or loose drawing to the front: here at once, and for everyone unless `server` says the server does
   * that itself.
   */
  function toFront(id: string, server = true) {
    // A press and the click after it both call this; ask the server only once.
    const asked = picked?.id === id && picked.over === top
    setPicked({ id, over: top })
    if (!server || readOnly || top === id || asked) return
    const drawing = board.drawings.some((d) => d.id === id)
    void run(() => (drawing ? api.bringDrawingToFront(id) : api.bringBucketToFront(id)))
  }
  /** Cards in a collapsed bucket are folded away with it: not on the board until it opens again. */
  const { collapsedIds, visibleCards } = useMemo(() => {
    const collapsedIds = new Set(board.buckets.filter((b) => b.collapsed).map((b) => b.id))
    return { collapsedIds, visibleCards: board.cards.filter((n) => !n.bucketId || !collapsedIds.has(n.bucketId)) }
  }, [board.buckets, board.cards])
  const byId = useMemo(() => itemsById(board), [board])
  const search = useBoardSearch({ board, visibleCards, collapsedIds, filterFaded: filters.faded, expand })
  const { terms, matches, results, current } = search
  const boardView = useBoardView({
    scrollRef,
    canvasRef,
    searching: !!search.state,
    // A quick-edit window sits next to the card on screen; after zooming it would point at nothing.
    onZoom: () => setQuickEdit(null),
  })
  const { margin, zoom, zoomTo, toCanvas, boardPoint } = boardView
  /** The strokes being drawn with the pen and saved; while it is the tool, they all go into one drawing. */
  const pen = useStroke(toCanvas, tool === 'pen')
  /** Whether a stroke is being drawn or saved. */
  const stroke = pen.strokes.length > 0

  // Background refreshes would fight with a drag in progress.
  useEffect(() => setPaused(gesture !== null || stroke || cardBusy || trash.busy), [gesture, stroke, cardBusy, trash.busy, setPaused])

  // Live geometry: the server's layout, with the drags in progress applied on top (yours wins over someone else's).
  const dragging = draggingOf(gesture)
  const drags = activeDrags(board, dragging, arrangeDrop?.spots, remoteDrags)
  const { bucketRect, cardPoint, imageRect, drawingRect, liveRect, remoteRects } = liveGeometry(board, drags, dragging ? arrangeDrop : null)
  /** What your drag in progress lifts: one item, or the ones selected with it. */
  const liftedIds = new Set(!dragging || dragging.done ? [] : dragging.kind === 'group' ? dragging.members.map((m) => m.id) : [dragging.id])
  const lifting = (id: string) => liftedIds.has(id)
  // Tell the others about your drag while it is in progress (as a string, so an unchanged drag sends nothing).
  const sent = sentDrag(dragging)
  useEffect(() => onDrag?.(sent ? (JSON.parse(sent) as DragUpdate) : null), [sent, onDrag])

  /**
   * A card put at a point, naming the front bucket when it contains the card's center there: by position alone the
   * server would put it in the bucket stacked on top, which this screen may show behind the front one.
   */
  function cardPlace(at: Point): Point & { bucketId?: string } {
    if (!front) return at
    const shown = stacked.map((b) => ({ ...b, ...bucketRect(b) }))
    return bucketUnderCard(shown, at, null, front)?.id === front ? { ...at, bucketId: front } : at
  }
  /** The bucket shown under the pointer at a canvas point, which what is let go there joins (`bucketAtPointer`). */
  const bucketAt = (pointer: Point) => bucketAtPointer(drawnBuckets.map((b) => ({ ...b, ...bucketRect(b) })), pointer)
  /**
   * What a drop with the pointer at a canvas point tells the server about an item landing at `item`: that bucket, or
   * loose (`DropBucket`). An item that doesn't reach into the bucket (one of a group, away from the pointer) stays loose.
   */
  function dropBucket(pointer: Point, item?: Rect): DropBucket {
    const bucket = bucketAt(pointer)
    return bucket && (!item || intersects(bucketRect(bucket), item)) ? { bucketId: bucket.id } : { loose: true }
  }
  /** The cards on the board in the order they are drawn: bucket by bucket as the buckets are, loose ones last. */
  const drawn = drawOrder(visibleCards, drawnBuckets)
  const geometry = { bucketRect, cardPoint, imageRect, drawingRect }
  const probe = useDropProbe({ board, expanded, cardHandle: expandedRef, trash, railTrash: trashRef, toCanvas, drawn, drawnBuckets, ...geometry })
  const { onTrash, cardAt } = probe
  const moves = useBoardMoves({ board, selected, setSelected, expanded, closeCard: () => setExpanded(null), settle, cardPlace, dropBucket, ...geometry })
  const { deleteSelected, moveGroup, putItems } = moves

  const edits = useBoardEdits({
    board,
    runRec,
    settle,
    setSelection,
    setSelected,
    unselect,
    expanded,
    setExpanded,
    toFront,
    dropBucket,
    fill,
    setTool,
    viewportCenter: () => canvas.viewportCenter(),
  })
  const { editCard, deleteCard, deleteImage, deleteDrawing, addImages, addImagesFromPicker, renameTag, recolorTag, deleteTag } = edits

  /**
   * What the selection rectangle takes: over the board, the buckets and loose items it touches; inside a bucket, that
   * bucket's own items it touches. Folded-away items aren't on the board to take.
   */
  function inRectangle(rect: Rect, bucketId: string | null): Selection[] {
    const loose = (item: { bucketId: string | null }) => item.bucketId === bucketId
    const looseBuckets = bucketId ? [] : board.buckets.filter((b) => touches(rect, b.collapsed ? collapsedRect(b) : b))
    return [
      ...looseBuckets.map((b) => ({ kind: 'bucket' as const, id: b.id })),
      ...visibleCards.filter((n) => loose(n) && touches(rect, cardBox(n))).map((n) => ({ kind: 'card' as const, id: n.id })),
      ...images.filter(({ image, rect: r }) => loose(image) && touches(rect, r)).map(({ image }) => ({ kind: 'image' as const, id: image.id })),
      ...drawings.filter(({ drawing, rect: r }) => loose(drawing) && touches(rect, r)).map(({ drawing }) => ({ kind: 'drawing' as const, id: drawing.id })),
    ]
  }

  const newCard = useNewCardDrag({
    readOnly,
    onTrash,
    setOverTrash: (over) => setDrops((d) => ({ ...d, trash: over })),
    // Over the board, not over the open card or the trash.
    landing: (client) => (expandedRef.current?.contains(client) || onTrash(client) ? null : boardPoint(client)),
    onCarry: () => setQuickEdit(null),
    stick: edits.stickCard,
  })
  const drop = useBoardDrops({
    board,
    expanded,
    cardHandle: expandedRef,
    trashHandle: trash.ref,
    canvasRef,
    drops,
    arrangeDrop,
    setDrops,
    probe,
    setGesture,
    selected,
    setSelected,
    setSelection,
    setBinned,
    runRec,
    settle,
    notify,
    toCanvas,
    dropBucket,
    toFront,
    bucketRect,
    cardPoint,
    imageRect,
    drawingRect,
    deleteCard,
    deleteSelected,
    putItems,
    moveGroup,
  })

  function beginGesture(e: PointerEvent, what: GestureTarget) {
    setQuickEdit(null)
    const point = toCanvas(e)
    canvasRef.current!.setPointerCapture(e.pointerId)
    shaken.current = shakeDetector()
    setGesture({ ...what, start: point, current: point, home: homeOf(what) ?? undefined })
  }

  /** The area a card or group being dragged takes up as the drag begins: released back over it, nothing changes. */
  function homeOf(what: GestureTarget): Rect | null {
    const items = what.kind === 'card' ? [{ kind: 'card' as const, id: what.id }] : what.kind === 'group' ? what.members : []
    return bounds(items.flatMap((m) => liveRect(m.kind, m.id) ?? []))
  }

  /**
   * A press on an item: selects it and starts dragging it (`handle`: resizing from that corner or edge). Pressing one of
   * several selected items drags them all; with Shift or Ctrl/Cmd the item joins or leaves the selection instead.
   */
  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: ItemGesture, id: string, { handle }: { handle?: ResizeHandle } = {}) {
    // With Space held, the press goes on to the canvas, which pans.
    if (e.button !== 0 || tool !== 'select' || spaceHeld) return
    cardPresses.current = [cardPresses.current[1], kind === 'card' ? id : null]
    e.stopPropagation()
    e.preventDefault()
    // preventDefault keeps focus where it is, so let a title being edited go (it saves on blur).
    if (titleEdit && document.activeElement instanceof HTMLElement) document.activeElement.blur()
    if (kind === 'card') search.lookAt(id)
    // Buckets and loose drawings come to the front when pressed, for viewers here only.
    const pressed = itemKindOf[kind]
    if (pressed === 'bucket' || (pressed === 'drawing' && board.drawings.some((d) => d.id === id && isLooseDrawing(d)))) toFront(id)
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
    const item: Selection = { kind: pressed, id }
    const resize = kind === 'resize' || kind === 'image-resize'
    if (!resize && adds(e)) {
      titlePress.current = null
      setSelected(togglePicked(selected, item))
      return
    }
    if (!resize && selected.length > 1 && isSelected(item.kind, id)) {
      // What is in a bucket that moves along goes with it, so it doesn't move itself.
      const moving = new Set(selected.filter((s) => s.kind === 'bucket').map((s) => s.id))
      const members = selected.filter((s) => s.kind === 'bucket' || !moving.has(bucketOf(board, s) ?? ''))
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

  /** A press on the canvas, by the tool picked: a pan, a pen stroke, a new bucket or card, or a selection rectangle. */
  function onCanvasPointerDown(e: PointerEvent) {
    cardPresses.current = [cardPresses.current[1], null]
    // The middle button pans, and so does the left one with Space held (the wheel zooms, Shift + wheel scrolls).
    if (e.button === 1 || (e.button === 0 && spaceHeld)) {
      e.preventDefault()
      pan.start(e)
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
      // The new card's top-left corner goes where you clicked, so you can tell where it goes.
      edits.stickCard(snapPoint(toCanvas(e)), toCanvas(e))
    } else if (readOnly) {
      setSelection(null)
    } else {
      // A click without dragging lets go of the selection. No text gets selected on the way, and focus leaves whatever
      // had it, as a press on empty space does.
      e.preventDefault()
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur()
      beginGesture(e, { kind: 'select', bucketId: null, base: adds(e) ? selected : [] })
    }
  }

  function onCanvasPointerMove(e: PointerEvent) {
    lastPointer.current = toCanvas(e)
    // Touch has no pointer to show, only drags (which pin the thumbtack).
    if (e.pointerType !== 'touch') pointing.point({ x: e.clientX, y: e.clientY })
    if (tool === 'pen') movePenRing(penRing.current, toCanvas(e), e.pointerType)
    if (pan.move(e)) return
    if (pen.drawing()) {
      pen.extend(e)
      return
    }
    if (!gesture || gesture.done) return
    const current = toCanvas(e)
    const next = { ...gesture, current, away: gesture.away || (!!gesture.home && !contains(gesture.home, current)), dragged: isDrag({ ...gesture, current }) }
    const client = { x: e.clientX, y: e.clientY }
    if (next.kind === 'select') {
      setGesture(next)
      if (isDrag(next)) setSelected(mergePicked(next.base, inRectangle(spanRect(next.start, next.current), next.bucketId)))
    } else if (next.kind === 'group') {
      // Cards shaken together line up in a grid around the one held, which then moves with the pointer.
      const shake = !next.grid && shaken.current(e.clientX, e.clientY, e.timeStamp)
      const moved = shake ? { ...next, grid: gridFor(board, next.id, next.members) } : next
      setGesture(moved)
      setDrops(dropTargetsFor(moved, client, probe))
    } else {
      setGesture(next)
      setDrops(dropTargetsFor(next, client, probe))
    }
  }

  function onCanvasPointerUp(e: PointerEvent) {
    if (pan.end()) return
    if (pen.drawing()) {
      pen.finish(runRec, board.id)
      return
    }
    if (!gesture || gesture.done) return
    const client = { x: e.clientX, y: e.clientY }
    switch (gesture.kind) {
      case 'select':
        return releaseSelect(gesture)
      case 'group':
        return releaseGroup(gesture, client)
      case 'draw':
        return releaseDraw(gesture)
      default:
        return releaseItem(gesture, client)
    }
  }

  /**
   * Ends the selection rectangle. A click without a drag: inside a bucket it selects the bucket (or, adding, it joins
   * or leaves the selection); on the board it keeps only the selection it was adding to (none without Shift or Ctrl/Cmd).
   */
  function releaseSelect(g: Extract<Gesture, { kind: 'select' }>) {
    setGesture(null)
    if (isDrag(g)) return
    setSelected(g.bucketId ? togglePicked(g.base, { kind: 'bucket', id: g.bucketId }) : g.base)
  }

  /** Ends a group drag. A click on one of several selected selects just that one (a click on a card's title edits it, as ever). */
  function releaseGroup(g: GroupGesture, client: Point) {
    if (isDrag(g)) return drop.dropGroup(g, client)
    setGesture(null)
    setSelection({ kind: g.held, id: g.id })
    const onTitle = g.held === 'card' && titlePress.current === g.id
    if (onTitle) setTitleEdit({ id: g.id, caret: caretAt(client.x, client.y) })
  }

  /** Adds the bucket drawn with the bucket tool, unless it is too small. */
  function releaseDraw(g: Extract<Gesture, { kind: 'draw' }>) {
    const rect = spanRect(snapPoint(g.start), snapPoint(g.current))
    if (rect.width < MIN_BUCKET_WIDTH || rect.height < MIN_BUCKET_HEIGHT) {
      setGesture(null)
      return
    }
    setGesture({ ...g, done: true })
    edits.addBucket(rect)
  }

  /** Ends a drag of one item, on what dropTargetsFor found. A click on a card's title edits it, the text cursor where the click was. */
  function releaseItem(g: ItemDrag, client: Point) {
    if (!isDrag(g)) {
      setGesture(null)
      const id = titlePress.current
      if (g.kind === 'card' && id === g.id) setTitleEdit({ id, caret: caretAt(client.x, client.y) })
      return
    }
    drop.dropItem(g, client)
  }

  // Buckets are drawn as they stack, so where they overlap the one on top covers the others. What was dropped on the
  // trash is gone at once.
  const buckets = stacked.filter((b) => !binned.includes(b.id)).map((bucket) => ({ bucket, rect: bucketRect(bucket) }))
  /** Folded away with the cards in a collapsed bucket, or attached to a card. */
  const hidden = (item: { id: string; cardId: string | null; bucketId: string | null }) =>
    item.cardId !== null || (!!item.bucketId && collapsedIds.has(item.bucketId)) || binned.includes(item.id)
  /** The images on the board (the same list while nothing about them changes, for the minimap). */
  const shownImages = useMemo(
    () => board.images.filter((i) => i.cardId === null && !(i.bucketId && collapsedIds.has(i.bucketId)) && !binned.includes(i.id)),
    [board.images, collapsedIds, binned],
  )
  const images = shownImages.map((image) => ({ image, rect: imageRect(image) }))
  const drawings = board.drawings.filter((d) => !hidden(d)).map((drawing) => ({ drawing, rect: drawingRect(drawing) }))
  const bucketsById = new Map(buckets.map((b) => [b.bucket.id, b]))
  const drawingsById = new Map(drawings.map((d) => [d.drawing.id, d]))
  /** What the board's items call back; the same functions every render, so the memoized items don't render again for them. */
  const itemHandlers = useStableHandlers<BoardItemHandlers>({
    pointerDown: (e, kind, id, options) => onItemPointerDown(e, kind, id, options),
    expand,
    renameTitle: (card, title) => editCard(card, { title }),
    titleDone: () => setTitleEdit(null),
    quickEdit: (cardId, kind, anchor) => setQuickEdit({ cardId, kind, anchor }),
    deleteImage,
    reshapeDrawing: (drawing, reshape) => settle(...ops.reshapeDrawing(drawing, reshape)),
    bucketPress: onBucketPointerDown,
    pickBucket(e, bucket) {
      if (adds(e)) return
      setSelection({ kind: 'bucket', id: bucket.id })
      toFront(bucket.id)
    },
    toggleBucket: (bucket) => edits.toggleCollapsed(bucket),
  })
  /** What the hidden list of every card calls back (CardIndex): a card focused there is selected and brought on screen. */
  const indexHandlers = useStableHandlers({
    focusCard(card: CardDetail) {
      setSelection({ kind: 'card', id: card.id })
      search.revealSoon(card.id)
    },
    openCard: expand,
  })
  const drawingView = ({ drawing, rect }: (typeof drawings)[number]) => (
    <BoardDrawing
      key={drawing.id}
      drawing={drawing}
      rect={rect}
      selected={isSelected('drawing', drawing.id)}
      lifted={lifting(drawing.id)}
      reshapeZoom={readOnly || selection?.id !== drawing.id ? undefined : zoom}
      handlers={itemHandlers}
    />
  )
  /** How each tag shows: its saved color, or one being saved or previewed. */
  const tagColors = useMemo(() => tagColorsOf(board.tags, edits.tagSaving, tagPreview), [board.tags, edits.tagSaving, tagPreview])
  /** How many images are attached to each card. */
  const attached = useMemo(() => {
    const counts = new Map<string, number>()
    for (const image of board.images) if (image.cardId) counts.set(image.cardId, (counts.get(image.cardId) ?? 0) + 1)
    return counts
  }, [board.images])
  /** How many cards, images and drawings each bucket holds. */
  const counts = useMemo(() => bucketCounts(board.cards, board.images, board.drawings), [board.cards, board.images, board.drawings])
  // Bucket by bucket as they are drawn, loose ones last, most recently updated on top.
  const cards = drawn.filter((n) => !binned.includes(n.id)).map((card) => ({ card, point: cardPoint(card) }))
  /**
   * The cards, images and drawings drawn right above each bucket, by its id, so a bucket drawn above covers them; under
   * null the loose cards and images (and what is in a bucket that isn't shown), drawn above every bucket.
   */
  const inBucket = new Map<string | null, { cards: typeof cards; images: typeof images; drawings: typeof drawings }>()
  const groupOf = (bucketId: string | null) => {
    const key = bucketId !== null && bucketsById.has(bucketId) ? bucketId : null
    let group = inBucket.get(key)
    if (!group) inBucket.set(key, (group = { cards: [], images: [], drawings: [] }))
    return group
  }
  for (const shown of images) groupOf(shown.image.bucketId).images.push(shown)
  for (const shown of cards) groupOf(shown.card.bucketId).cards.push(shown)
  for (const shown of drawings) if (!isLooseDrawing(shown.drawing)) groupOf(shown.drawing.bucketId).drawings.push(shown)
  /** The topmost bucket's layer: loose cards and images are drawn right above it, below loose drawings stacked higher. */
  const lastBucketLayer = layers.findLastIndex((l) => l.kind === 'bucket' && bucketsById.has(l.id))
  const canvas = useCanvasExtent(boardView, [
    ...buckets.map((b) => b.rect),
    ...cards.map((n) => cardBox(n.point)),
    ...images.map((i) => i.rect),
    ...drawings.map((d) => d.rect),
  ])
  const { origin, extent } = canvas
  /** The part of the board around the screen (useVisibleWindow): only what touches it is on the page, unless it is kept. */
  const near = useVisibleWindow(boardView.view, zoom, origin)
  /** On the page wherever they are: what is selected, moving, edited or looked at, so focus and gestures stay with them. */
  const kept = new Set([...selected.map((s) => s.id), ...liftedIds, ...remoteIds, ...[titleEdit?.id, quickEdit?.cardId, current].filter((id): id is string => !!id)])
  const onPage = (id: string, rect: Rect) => kept.has(id) || intersects(near, rect)
  /** Zoomed far out, cards show only their fill, title and tag colors. */
  const far = zoom < FAR_ZOOM
  useReveal(search, { scrollRef, boardTop, board, zoom, origin, cardPoint })
  const pointing = usePointing({ scrollRef, toCanvas, onCursor })

  // The others see your pins in what you select, until you select something else.
  const selectedIds = selected.map((s) => s.id).join(' ')
  useEffect(() => onSelect?.(selectedIds ? selectedIds.split(' ') : []), [selectedIds, onSelect])
  useEffect(() => () => onSelect?.([]), [onSelect])
  /** Where an item someone else has selected is shown now (moving with drags); null when it is gone or folded away. */
  const pinPlace = (id: string): PinPlace | null => {
    // By id, not liveRect: someone may have selected the whole board, and this runs for each of their pins.
    const found = byId.get(id)
    if (!found) return null
    switch (found.kind) {
      case 'bucket':
        return { rect: found.item.collapsed ? collapsedRect(bucketRect(found.item)) : bucketRect(found.item), card: false }
      case 'card':
        return found.item.bucketId && collapsedIds.has(found.item.bucketId) ? null : { rect: cardBox(cardPoint(found.item)), card: true }
      case 'image':
        return hidden(found.item) ? null : { rect: imageRect(found.item), card: false }
      case 'drawing':
        return hidden(found.item) ? null : { rect: drawingRect(found.item), card: false }
    }
  }

  // Someone picked in the top bar: glide to their thumbtack, which pulses and shows their name for a moment.
  const [focusedTack, setFocusedTack] = useState<string | null>(null)
  const goTo = useRef<(principalId: string) => void>(() => {})
  useEffect(() => {
    goTo.current = (principalId) => {
      const peers = cursors?.snapshot()
      const found = peers && whereIs(headsOf(peers.cursors, viewers), pinsOf(peers, viewers, pinPlace, zoom), principalId)
      if (!found) return
      canvas.navigate(found.at, { smooth: true })
      setFocusedTack(found.key)
    }
  })
  useEffect(() => {
    if (focus) goTo.current(focus.principalId)
  }, [focus])
  useEffect(() => {
    if (!focusedTack) return
    const timer = window.setTimeout(() => setFocusedTack(null), FOCUS_MS)
    return () => window.clearTimeout(timer)
  }, [focusedTack, focus])

  /** Image files dropped on the board go where they were dropped. */
  const fileDrop = useFileDrop(!readOnly, (files, e) => {
    const at = toCanvas(e)
    void addImages(files, { at: { x: at.x + IMAGE_OFFSET.x - 20, y: at.y + IMAGE_OFFSET.y - 20 } })
  })
  const { copySelected, duplicateCard } = useBoardClipboard({
    board,
    readOnly,
    selected,
    setSelected,
    setSelection,
    settle,
    notify,
    lastPointer,
    moveGroup,
    addImages,
    expanded,
    cardHandle: expandedRef,
  })
  useBoardKeys({
    readOnly,
    cardOpen: !!expanded,
    selected,
    selection,
    busy: { gesture: !!gesture, stroke, card: cardBusy },
    setTool,
    setSelection,
    copySelected,
    deleteSelected: () => void deleteSelected(),
    deleteOne: edits.deleteOne,
    duplicateCard,
    addImagesFromPicker: () => void addImagesFromPicker(),
    undo,
  })

  /** Where a card dragged out of the trash window would land. */
  const outgoingPlace = trash.outgoing ? trash.landing(trash.outgoing) : null
  const resizing = gesture && (gesture.kind === 'resize' || gesture.kind === 'image-resize') && !gesture.done ? gesture : null
  const draggedCard = dragging?.kind === 'card' ? cards.find((n) => n.card.id === dragging.id) : undefined
  /**
   * A card, image or drawing being dragged by itself: its bucket, its saved place (outlined while it moves) and where it
   * is shown now. Not once it is dropped: an image may be on its way into a card.
   */
  const draggedItem = (() => {
    if (dragging?.done) return undefined
    switch (dragging?.kind) {
      case 'card':
        return draggedCard && { bucketId: draggedCard.card.bucketId, home: cardBox(draggedCard.card), now: cardBox(draggedCard.point) }
      case 'image': {
        const found = images.find((i) => i.image.id === dragging.id)
        return found && { bucketId: found.image.bucketId, home: found.image, now: found.rect }
      }
      case 'drawing': {
        const found = drawings.find((d) => d.drawing.id === dragging.id)
        return found && { bucketId: found.drawing.bucketId, home: found.drawing, now: found.rect }
      }
    }
  })()
  /** Several selected items being dragged together (the trash can shows for them too). */
  const draggedGroup = dragging?.kind === 'group' && !dragging.done
  /** Around several selected cards, for the toolbar above them; null with fewer than two. */
  const selectedCards = cards.filter(({ card }) => isSelected('card', card.id))
  const selectedCardsRect = selectedCards.length >= 2 ? bounds(selectedCards.map((n) => cardBox(n.point))) : null
  /** The card a dragged image would be attached to (not while it is over the open card's window). */
  const imageTarget = dragging?.kind === 'image' && !dragging.done && !incoming ? cardAt(dragging.current) : undefined
  /** Whether the item dragged goes elsewhere than a place on the board: into a card, the open card, the trash or a collapsed bucket. */
  const elsewhere = linkDrop || overTrash || deletedDrop || foldDrop || imageTarget || incoming || incomingDrawing
  /** The bucket an item being dragged, or a new card carried from the rail, would go into: the one under the pointer. */
  const dropTarget = draggedItem && !elsewhere
    ? bucketAt(dragging!.current)
    : newCard.pointer
      ? bucketAt(newCard.pointer)
      : undefined
  /** The bucket a group of cards would go into, while it is dragged. */
  const arrangingInto = arrangeDrop && !dragging?.done ? arrangeDrop.bucket.id : null
  /** The new bucket being drawn. */
  const draft = gesture?.kind === 'draw' && !gesture.done ? spanRect(snapPoint(gesture.start), snapPoint(gesture.current)) : null
  /** The selection rectangle being dragged out. */
  const marquee = gesture?.kind === 'select' && isDrag(gesture) ? spanRect(gesture.start, gesture.current) : null
  /** While a card or several items are dragged, or a new card carried, the rail's card button is a trash can. */
  const trashShown = (draggedCard && !dragging?.done) || draggedGroup || newCard.carried

  const selectedBucket = selection?.kind === 'bucket' ? board.buckets.find((b) => b.id === selection.id) : undefined
  const selectedImage = selection?.kind === 'image' ? board.images.find((i) => i.id === selection.id) : undefined
  const selectedDrawing = selection?.kind === 'drawing' ? drawings.find((d) => d.drawing.id === selection.id) : undefined
  const expandedCard = address.openCard
  const countIn = (bucketId: string): BucketCount => counts.get(bucketId) ?? NOTHING_IN
  /** Cards the filters or the search fade. */
  const dimmed = (card: CardDetail) => filters.faded(card) || search.misses(card)
  // Which cards are faded, as one string, so the minimap's cards only render again when that changes.
  const mapFaded = visibleCards.flatMap((n) => (dimmed(n) ? [n.id] : [])).join(' ')
  const historyBusy = !!gesture || stroke || cardBusy
  /** The undo and redo buttons, on the board and in the open card; none for viewers. */
  const history: HistoryButtons | undefined = readOnly
    ? undefined
    : { undo: { label: undo.nextUndo, disabled: historyBusy, onClick: undo.undo }, redo: { label: undo.nextRedo, disabled: historyBusy, onClick: undo.redo } }
  /** What is drawn with a bucket, near the screen: its images, cards and drawings; for null the loose cards and images. */
  function itemViews(bucketId: string | null) {
    const group = inBucket.get(bucketId)
    if (!group) return []
    return [
      ...group.images
        .filter(({ image, rect }) => onPage(image.id, rect))
        .map(({ image, rect }) => (
          <BoardImage
            key={image.id}
            image={image}
            rect={rect}
            selected={isSelected('image', image.id)}
            lifted={lifting(image.id)}
            resizable={!readOnly && selectedImage?.id === image.id}
            readOnly={readOnly}
            handlers={itemHandlers}
          />
        )),
      ...group.cards
        .filter(({ card, point }) => onPage(card.id, cardBox(point)))
        .map(({ card, point }) => (
          <BoardCard
            key={card.id}
            card={card}
            point={point}
            images={attached.get(card.id)}
            tagColors={tagColors}
            selected={isSelected('card', card.id)}
            lifted={lifting(card.id) || remoteMoving(card.id)}
            dimmed={dimmed(card)}
            highlight={matches.has(card.id) ? terms : undefined}
            current={card.id === current}
            dropTarget={
              imageTarget?.id === card.id ||
              (linkDrop?.window === false && linkDrop.card.id === card.id) ||
              (attachDrop?.window === false && attachDrop.card.id === card.id)
            }
            editingTitle={titleEdit?.id === card.id}
            titleCaret={titleEdit?.id === card.id ? titleEdit.caret : undefined}
            readOnly={readOnly}
            far={far}
            handlers={itemHandlers}
          />
        )),
      ...group.drawings.filter(({ drawing, rect }) => onPage(drawing.id, rect)).map(drawingView),
    ]
  }
  const hint = edits.uploads > 0 ? 'Uploading…' : fileDrop.over ? 'Drop to add the image here' : readOnly ? undefined : hints[tool]

  return (
    <section className="board" aria-label={board.name}>
      <div className={`board-scroll${fileDrop.over ? ' file-over' : ''}`} ref={scrollRef} {...fileDrop.handlers}>
        <div
          className="canvas-sizer"
          style={{ width: origin.x + extent.width * zoom + margin.x, height: origin.y + extent.height * zoom + margin.y }}
        >
          <div
            ref={canvasRef}
            className={`canvas tool-${tool}${far ? ' far' : ''}${pan.panning ? ' panning' : spaceHeld ? ' space-pan' : ''}`}
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
            onPointerLeave={() => {
              movePenRing(penRing.current, null)
              pointing.leave()
            }}
            onPointerUp={onCanvasPointerUp}
            onPointerCancel={() => {
              setGesture(null)
              pen.cancel()
              pan.end()
              setDrops(NO_DROPS)
            }}
            // Losing the pointer without a release ends a pan or an unfinished drag, so nothing is left hanging mid-air.
            // A release loses it too, right after: a finished drag keeps its gesture and a group its spots in a bucket.
            onLostPointerCapture={() => {
              pan.end()
              setGesture((g) => (g && !g.done ? null : g))
              pen.cancel()
              setDrops(released)
            }}
          >
            {lastBucketLayer < 0 && itemViews(null)}
            {layers.flatMap((layer, i) => {
              if (layer.kind === 'drawing') {
                const shown = drawingsById.get(layer.id)
                return shown && onPage(shown.drawing.id, shown.rect) ? [drawingView(shown)] : []
              }
              const shown = bucketsById.get(layer.id)
              if (!shown) return []
              const { bucket, rect } = shown
              const own = bucket.collapsed
                ? [
                    <BoardCollapsedBucket
                      key={bucket.id}
                      bucket={bucket}
                      rect={rect}
                      count={countIn(bucket.id)}
                      dropTarget={foldDrop === bucket.id}
                      hits={search.hits.get(bucket.id) ?? 0}
                      selected={isSelected('bucket', bucket.id)}
                      readOnly={readOnly}
                      handlers={itemHandlers}
                    />,
                  ]
                : [
                    <BoardBucket
                      key={bucket.id}
                      bucket={bucket}
                      rect={rect}
                      count={countIn(bucket.id)}
                      dropTarget={dropTarget?.id === bucket.id || arrangingInto === bucket.id}
                      searchHit={search.hits.has(bucket.id)}
                      selected={isSelected('bucket', bucket.id)}
                      readOnly={readOnly}
                      resizable={selectedBucket?.id === bucket.id}
                      handlers={itemHandlers}
                    />,
                    ...itemViews(bucket.id),
                  ]
              return i === lastBucketLayer ? [...own, ...itemViews(null)] : own
            })}

            {draft && (
              <div
                className={`bucket-draft${draft.width < MIN_BUCKET_WIDTH || draft.height < MIN_BUCKET_HEIGHT ? ' too-small' : ''}`}
                style={{ left: draft.x, top: draft.y, width: draft.width, height: draft.height }}
              />
            )}

            {newCard.spot && <Slot rect={cardBox(newCard.spot)} />}
            {outgoingPlace && <Slot rect={cardBox(outgoingPlace)} />}
            {draggedItem && <Slot rect={draggedItem.home} />}

            {/* Above everything while it is drawn; once saved, the drawing takes its place in the stack. */}
            <div className="pen-strokes">
              {pen.strokes.map((s, i) => (
                <StrokeSvg key={i} points={s.points} color={inkColor[s.color]} strokeWidth={s.width} at={{ x: 0, y: 0 }} />
              ))}
            </div>

            {tool === 'pen' && !readOnly && <PenRing ringRef={penRing} width={strokeWidth} zoom={zoom} />}

            {selectedCardsRect && !readOnly && !gesture && <SelectionToolbar rect={selectedCardsRect} zoom={zoom} onSort={moves.sortCards} />}

            {selectedDrawing && !readOnly && !(dragging?.id === selectedDrawing.drawing.id && !dragging.done) && (
              <DrawingToolbar
                drawing={selectedDrawing.drawing}
                rect={selectedDrawing.rect}
                zoom={zoom}
                onRecolor={(color) => edits.recolorDrawing(selectedDrawing.drawing, color)}
                onDelete={() => deleteDrawing(selectedDrawing.drawing.id)}
              />
            )}

            <RemoteDragOutlines drags={remoteDrags.filter((r) => !r.endedAt && r.id !== dragging?.id)} rectsOf={remoteRects} unnamed={!!onCursor} />
            {cursors && onCursor && (
              <RemoteCursors cursors={cursors} viewers={viewers} drags={remoteDrags} placeOf={pinPlace} near={near} zoom={zoom} focused={focusedTack} />
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

      <CardIndex cards={visibleCards} buckets={board.buckets} onFocusCard={indexHandlers.focusCard} onOpenCard={indexHandlers.openCard} />
      <ToolRail
        tool={tool}
        onToolChange={setTool}
        fill={fill}
        onFillChange={setFill}
        ink={ink}
        onInkChange={setInk}
        strokeWidth={strokeWidth}
        onStrokeWidthChange={setStrokeWidth}
        onCardPointerDown={newCard.onPointerDown}
        trash={trashShown ? (overTrash ? 'over' : 'shown') : undefined}
        trashRef={trashRef}
        onAddImage={() => void addImagesFromPicker()}
        readOnly={readOnly}
        searching={!!search.state}
        onSearch={() => (search.state ? search.close() : search.open())}
      />
      <BoardFilters board={board} filters={filters} tagColors={tagColors} />
      <div className={`board-top${search.state ? ' searching' : ''}`} ref={boardTop}>
        {search.state && (
          <BoardSearch
            inputRef={search.inputRef}
            query={search.state.query}
            onQueryChange={search.changeQuery}
            terms={terms}
            results={results}
            current={current}
            listOpen={search.state.listOpen}
            onListOpenChange={search.setListOpen}
            onMove={search.move}
            onOpen={search.openResult}
            onClose={search.close}
            faded={board.cards.filter((n) => matches.has(n.id) && filters.faded(n)).length}
            folded={search.folded.length}
            onClearFilter={() => {
              filters.clear()
              search.revealSoon()
            }}
            bucketName={(card) => board.buckets.find((b) => b.id === card.bucketId)?.name ?? 'Loose'}
            values={{
              tag: board.tags.map((t) => t.name),
              assignee: board.people.map((p) => p.name),
              creator: board.cards.flatMap((n) => (n.createdBy ? [n.createdBy] : [])),
            }}
            tagColors={tagColors}
          />
        )}
        {readOnly && <div className="read-only-badge">View only</div>}
        {hint && <div className="canvas-hint">{hint}</div>}
      </div>
      <ViewControls
        undo={history?.undo}
        redo={history?.redo}
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={canvas.minimap.extent}
        view={canvas.minimap.view}
        viewport={canvas.minimap.viewport}
        items={(left, top, scale) => (
          <BoardMapItems
            left={left}
            top={top}
            scale={scale}
            buckets={board.buckets}
            cards={visibleCards}
            images={shownImages}
            current={current}
            dimmedKey={mapFaded}
          />
        )}
        onNavigate={canvas.navigate}
      />

      <BoardPanels
        board={board}
        tagColors={tagColors}
        bucket={selectedBucket}
        fresh={!!selection?.fresh}
        onSaveBucket={(bucket, { name = bucket.name }) => settle(...ops.renameBucket(bucket, name))}
        onDeleteBucket={(bucket) => {
          setSelection(null)
          void settle(...ops.deleteBucket(board, bucket))
        }}
        image={selectedImage}
        onSaveImage={(image, changes) => settle(...ops.editImage(image, changes, 'Edit caption', ['caption']))}
        onDeleteImage={(image) => deleteImage(image.id)}
        onClose={() => setSelection(null)}
        quickEdit={quickEdit}
        onCloseQuickEdit={() => setQuickEdit(null)}
        editCard={editCard}
        onRenameTag={renameTag}
        onRecolorTag={recolorTag}
        onPreviewTagColor={setTagPreview}
        onDeleteTag={deleteTag}
      />

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
          history={history}
          onBusy={setCardBusy}
          incoming={incoming}
          drawings={board.drawings.filter((d) => d.cardId === expandedCard.id)}
          incomingDrawing={incomingDrawing}
          linking={!!linkDrop?.window || !!attachDrop?.window}
          boardPoint={boardPoint}
          dropBucket={dropBucket}
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

      <BoardTrash
        board={board}
        trash={trash}
        readOnly={readOnly}
        tagColors={tagColors}
        zoom={zoom}
        dropOver={deletedDrop !== null}
        cardOpen={!!expandedCard}
        onCloseCard={() => setExpanded(null)}
        run={runRec}
        onCardBusy={setCardBusy}
        cardHandle={expandedRef}
        boardPoint={boardPoint}
      />

      {/* The board draws below the open card and the trash, so a card dragged over their windows is shown on top, at the pointer. */}
      {draggedCard && dragging && (linkDrop?.window || deletedDrop) && (
        <CardGhost
          card={draggedCard.card}
          client={deletedDrop ?? linkDrop!.client}
          grab={{ x: dragging.start.x - draggedCard.card.x, y: dragging.start.y - draggedCard.card.y }}
          zoom={zoom}
          images={attached.get(draggedCard.card.id)}
          tagColors={tagColors}
        />
      )}

      <Toast message={toast} onDone={clearToast} />

      {newCard.carried && <NewCardGhost client={newCard.carried} fill={fill} overTrash={overTrash} zoom={zoom} />}
    </section>
  )
}
