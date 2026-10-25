import { useEffect, useImperativeHandle, useLayoutEffect, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../api/client'
import type { DrawingDetail, DropBucket, ImageDetail, InkColor, CardComponent, CardComponentKind, CardDetail, Point, Rect, ResizeHandle } from '../api/types'
import { BoardImageView } from './BoardImageView'
import { inkColor, movePenRing, CARD_PEN, usePen } from './drawings'
import { DrawingGhost, DrawingToolbar, DrawingView, IncomingDrawingView, PenRing, StrokeSvg } from './DrawingView'
import { delta, isDrag } from './gesture'
import { ImageInspector } from './Inspector'
import { componentSize, defaultComponentSize, freeSpot, onCardCanvas, resizeFrom, resizeImage, snapPoint, spanRect } from './layout'
import { ComponentFrame, ChecklistComponent, LinksComponent, TextComponent } from './CardComponents'
import { CardBar } from './CardBar'
import { allOf, ops, runAll, settling } from './ops'
import { resizeCursor } from './resizeCursor'
import { componentMatches, type SearchTerm } from './search'
import { adds, bounds, insideClient, mergePicked, togglePicked, touches, useSelection, useSpaceHeld, type Picked } from './selection'
import { count } from './boardItems'
import { useFileDrop, usePan, useScrollStore } from './canvasHooks'
import { inField } from './keys'
import { deleted, patched, snap, step, target, type RecordingRun, type UndoChange } from './undo'
import { useStroke } from './useStroke'
import { useZoom } from './useZoom'
import { CardMapItems, ViewControls, type HistoryButtons } from './ViewControls'
import { stepZoom } from './zoom'

/** What the board needs from the card's canvas to drop images into it and paste there. */
export interface CardCanvasHandle {
  /** Whether a screen point is over the visible part of the canvas. */
  hits: (client: Point) => boolean
  /** A screen point in card pixels. */
  toCard: (client: Point) => Point
  /** Where pasted images go: the last pointer position over the canvas, or the middle of what is visible. */
  pastePoint: () => Point
}

/** A board image dragged over the canvas, at the top-left it would get here. */
export interface IncomingImage {
  image: ImageDetail
  at: Point
}

/** A board drawing dragged over the canvas, at the top-left it would get here. */
export interface IncomingDrawing {
  drawing: DrawingDetail
  at: Point
}

type Kind = 'component' | 'component-resize' | 'image' | 'image-resize' | 'drawing'

/** A selected component, image or drawing. */
type Item = Picked<'component' | 'image' | 'drawing'>

/** The kind of item a press drags. */
const itemKindOf: Record<Kind, Item['kind']> = {
  component: 'component',
  'component-resize': 'component',
  image: 'image',
  'image-resize': 'image',
  drawing: 'drawing',
}

/** A press on a component, image or drawing, in card pixels. */
interface Gesture {
  /** `group`: several selected items moved together; `select`: the selection rectangle. */
  kind: Kind | 'group' | 'select'
  /** The item pressed (empty for the selection rectangle). */
  id: string
  /** For group: the items moving. For select: the selection it adds to. */
  items?: Item[]
  /** For resizes: the corner or edge being dragged. */
  handle: ResizeHandle
  start: Point
  current: Point
  /** The last screen point. */
  client: Point
  /** The pointer is outside the window: an image or drawing dragged out, to drop on the board. */
  outside: boolean
  /** Finished: it stays until the board has reloaded, so the item doesn't jump back meanwhile. */
  done?: boolean
}

const NO_INSET = { x: 0, y: 0 }

/** Empty canvas kept beyond the furthest item: one grid step, so the canvas only outgrows the view when something sticks out. */
const CANVAS_MARGIN = 20

interface CardCanvasProps {
  card: CardDetail
  /** Images attached to the card. */
  images: ImageDetail[]
  /** Pen strokes on the card. */
  drawings: DrawingDetail[]
  /** The cards on the board, to link to. */
  cards: CardDetail[]
  readOnly?: boolean
  /** Search words to point out: components they occur in get an outline, and checklists and links mark them. */
  highlight?: SearchTerm[]
  handle: RefObject<CardCanvasHandle | null>
  /** Runs a change through the board (which then reloads), recorded for undo. */
  run: RecordingRun
  /** The board's undo and redo buttons, next to the minimap; none for viewers. */
  history?: HistoryButtons
  /** Whether a gesture is in progress, so the board holds back its refreshes. */
  onBusy: (busy: boolean) => void
  incoming: IncomingImage | null
  incomingDrawing: IncomingDrawing | null
  onAddImages: (files: File[], at?: Point) => void
  onPickImages: () => void
  /** A screen point in board canvas pixels when it is over the board, to drop an image or drawing there; else null. */
  boardPoint: (client: Point) => Point | null
  /** The bucket an image or drawing dropped on the board joins, by the pointer there (`dropBucket`). */
  dropBucket?: (pointer: Point, item?: Rect) => DropBucket
  boardZoom: number
  /** Asks to delete the whole card. */
  onDeleteCard: () => void
}

/** The card's own small board: its components, images and drawings, moved and resized like items on the board, with zoom and pan. */
export function CardCanvas(props: CardCanvasProps) {
  const { card, images, drawings, cards, readOnly, highlight, handle, run, history, onBusy, incoming, incomingDrawing, onAddImages, onPickImages, boardPoint, dropBucket, boardZoom, onDeleteCard } = props
  /** The selected items: one, or several (Shift or Ctrl/Cmd + click, or the selection rectangle). */
  const { selected, setSelected, selection, setSelection, unselect, isSelected } = useSelection<Item>()
  /** Space is held: a left press pans, as the middle button does. */
  const spaceHeld = useSpaceHeld()
  const [gesture, setGesture] = useState<Gesture | null>(null)
  /** The component added last: its main field is focused when it appears. */
  const [focusComponent, setFocusComponent] = useState<string | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  /** The scroll viewport in screen pixels: its size for the canvas's, where it is scrolled to for the minimap and new components. */
  const { size, view } = useScrollStore(scrollRef)
  const pan = usePan(scrollRef, canvasRef)
  const lastPointer = useRef<Point | null>(null)
  /** The images the last two presses were on: the canvas holds the pointer, so double-clicks reach it, not the image. */
  const imagePresses = useRef<[string | null, string | null]>([null, null])
  const { zoom, zoomTo } = useZoom(scrollRef, NO_INSET)
  // A zoom moved the scroll position in code: the minimap catches up before the browser paints.
  useLayoutEffect(() => view.refresh())
  /** The card's pen: on or off (while on, a press draws instead of selecting or moving), with an ink and size of its own. */
  const [penOn, setPenOn] = useState(false)
  const cardPen = usePen(CARD_PEN)
  const penRing = useRef<HTMLDivElement>(null)
  const drawingOn = !readOnly && penOn
  useEffect(() => () => onBusy(false), [onBusy])

  function toCard(client: Point): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: (client.x - rect.left) / zoom, y: (client.y - rect.top) / zoom }
  }

  /** The strokes being drawn with the pen and saved; while it is on, they all go into one drawing. */
  const pen = useStroke((e) => toCard({ x: e.clientX, y: e.clientY }), drawingOn)
  const penBusy = pen.strokes.length > 0
  useEffect(() => onBusy(gesture !== null || penBusy), [gesture, penBusy, onBusy])

  const hits = (client: Point) => !!scrollRef.current && insideClient(scrollRef.current.getBoundingClientRect(), client)
  /** What is visible of the canvas, in card pixels. */
  const visible = (): Rect => viewport(view.get())
  const viewport = (v: Rect): Rect => ({ x: v.x / zoom, y: v.y / zoom, width: v.width / zoom, height: v.height / zoom })

  /** Scrolls so a point on the card (picked on the minimap) is in the middle of the view. */
  function navigate(center: Point) {
    const el = scrollRef.current!
    el.scrollTo({ left: center.x * zoom - el.clientWidth / 2, top: center.y * zoom - el.clientHeight / 2 })
  }

  useImperativeHandle(handle, () => ({
    hits,
    toCard,
    pastePoint: () => {
      const v = visible()
      return lastPointer.current ?? { x: v.x + v.width / 2, y: v.y + v.height / 2 }
    },
  }))

  /** Runs a change through the board (recorded for undo as `describe` says), then lets go of the finished gesture. */
  const settle = settling(run, () => setGesture(null))

  const saveComponent = (component: CardComponent, changes: Parameters<typeof api.updateCardComponent>[2]) =>
    settle(...ops.editComponent(card.id, component, changes))

  function recolorDrawing(drawing: DrawingDetail, color: InkColor) {
    void settle(...ops.editDrawing(drawing, { color }, 'Change ink', ['colors']))
  }

  /** Adds a component in the first free spot in view (or, with none, wherever the server finds room) and focuses it. */
  function addComponent(kind: CardComponentKind) {
    const size = defaultComponentSize(kind)
    const spot = freeSpot([...card.components, ...images], size, visible())
    const [add, describe] = ops.addComponent(card.id, { kind, ...(spot ?? {}) })
    void settle(async () => {
      const component = await add()
      setFocusComponent(component.id)
      setSelection({ kind: 'component', id: component.id })
      return component
    }, describe)
  }

  /** Removes a component, image or drawing straight away; the toast that follows can undo it (and so can Ctrl/Cmd+Z). */
  function remove(kind: 'component' | 'image' | 'drawing', id: string) {
    unselect(id)
    const component = kind === 'component' ? card.components.find((b) => b.id === id) : undefined
    const image = kind === 'image' ? images.find((i) => i.id === id) : undefined
    const drawing = kind === 'drawing' ? drawings.find((d) => d.id === id) : undefined
    if (component) {
      // An empty component goes without a toast; it can still be undone.
      void settle(...ops.removeComponent(card.id, component))
    } else if (image) {
      void settle(...ops.deleteImage(image))
    } else if (drawing) {
      void settle(...ops.deleteDrawing(drawing))
    }
  }

  /** Removes the selected items at once, as one step that the toast can undo. */
  function removeSelected() {
    const items = selected
    setSelected([])
    const removeOne = async ({ kind, id }: Item): Promise<UndoChange | null> => {
      if (kind === 'component') {
        const component = card.components.find((b) => b.id === id)
        if (!component) return null
        await api.deleteCardComponent(card.id, id)
        return deleted(target.component(card.id, id), snap.component(component))
      }
      if (kind === 'image') {
        const image = images.find((i) => i.id === id)
        if (!image) return null
        await api.deleteImage(id)
        return deleted(target.image(id), snap.image(image))
      }
      const drawing = drawings.find((d) => d.id === id)
      if (!drawing) return null
      await api.deleteDrawing(id)
      return deleted(target.drawing(id), snap.drawing(drawing))
    }
    const what = count(items.length, 'item', 'items')
    void runAll(settle, `Remove ${what}`, `${what} removed`, (changes) =>
      allOf(items.map(async (item) => changes.push(await removeOne(item)))),
    )
  }

  /** Saves a group move as one step: each item goes where the drag shows it. */
  function moveGroup(items: Item[]) {
    // Work out every place now, as the drag shows it, before any request changes the card.
    const requests = items.flatMap(({ kind, id }) => {
      const component = kind === 'component' ? card.components.find((b) => b.id === id) : undefined
      const image = kind === 'image' ? images.find((i) => i.id === id) : undefined
      const drawing = kind === 'drawing' ? drawings.find((d) => d.id === id) : undefined
      if (component) {
        const { x, y } = componentRect(component)
        return [
          async () => {
            const after = await api.updateCardComponent(card.id, id, { x, y })
            return patched(target.component(card.id, id), snap.component(component), snap.component(after), ['x', 'y'])
          },
        ]
      }
      if (image) {
        const { x, y } = imageRect(image)
        return [
          async () => {
            const after = await api.updateImage(id, { x, y })
            return patched(target.image(id), snap.image(image), snap.image(after), ['x', 'y'])
          },
        ]
      }
      if (drawing) {
        const { x, y } = drawingRect(drawing)
        return [
          async () => {
            const after = await api.updateDrawing(id, { x, y })
            return patched(target.drawing(id), snap.drawing(drawing), snap.drawing(after), ['x', 'y'])
          },
        ]
      }
      return []
    })
    void runAll(settle, `Move ${count(items.length, 'item', 'items')}`, undefined, (changes) =>
      allOf(requests.map(async (request) => changes.push(await request()))),
    )
  }

  /** Dropped files are attached where they land; files over the band are handled by the window. */
  const fileDrop = useFileDrop(!readOnly, (files, e) => onAddImages(files, toCard({ x: e.clientX, y: e.clientY })), { stop: true })

  /** What the selection rectangle takes: the components, images and drawings it touches. */
  function inRectangle(rect: Rect): Item[] {
    return [
      ...card.components.filter((b) => touches(rect, b)).map((b) => ({ kind: 'component' as const, id: b.id })),
      ...images.filter((i) => touches(rect, i)).map((i) => ({ kind: 'image' as const, id: i.id })),
      ...drawings.filter((d) => touches(rect, d)).map((d) => ({ kind: 'drawing' as const, id: d.id })),
    ]
  }

  // Live geometry: the server's layout with your gesture applied, snapped as the server will.
  const dragging = gesture && gesture.kind !== 'select' && isDrag(gesture) ? gesture : null
  /** Whether an item moves with the drag in progress: the one dragged, or one of several selected moving together. */
  const moves = (id: string) => !!dragging && (dragging.kind === 'group' ? !!dragging.items?.some((i) => i.id === id) : dragging.id === id)
  /** An image or drawing dragged out of the window, to drop on the board. */
  const leaving = (dragging?.kind === 'image' || dragging?.kind === 'drawing') && !dragging.done && dragging.outside ? dragging : null

  function componentRect(component: CardComponent): Rect {
    if (!dragging || !moves(component.id)) return component
    const d = delta(dragging)
    const moved = { x: component.x + d.x, y: component.y + d.y }
    if (dragging.kind === 'component' || dragging.kind === 'group') return { ...component, ...onCardCanvas(moved, component) }
    const resized = resizeFrom(component, dragging.handle, d, componentSize)
    return { ...component, ...resized, ...onCardCanvas(resized, resized) }
  }

  function imageRect(image: ImageDetail): Rect {
    if (!dragging || !moves(image.id) || leaving) return image
    const d = delta(dragging)
    const moved = { x: image.x + d.x, y: image.y + d.y }
    if (dragging.kind === 'image' || dragging.kind === 'group') return { ...image, ...onCardCanvas(moved, image) }
    const resized = resizeImage(image, dragging.handle, d)
    return { ...image, ...resized, ...onCardCanvas(resized, resized) }
  }

  function drawingRect(drawing: DrawingDetail): Rect {
    if (!dragging || !moves(drawing.id) || leaving) return drawing
    const d = delta(dragging)
    return { ...drawing, ...onCardCanvas({ x: drawing.x + d.x, y: drawing.y + d.y }, drawing) }
  }

  /**
   * A press on a component's header, an image or a drawing: selects it and starts dragging it. Pressing one of several
   * selected items drags them all; with Shift or Ctrl/Cmd the item joins or leaves the selection instead.
   */
  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: Kind, id: string, handle: ResizeHandle = 'BottomRight') {
    // With the pen on, a press draws over whatever is there (onPenPointerDown took it already); with Space held, the
    // canvas pans.
    if (e.button !== 0 || drawingOn || spaceHeld) return
    imagePresses.current = [imagePresses.current[1], kind === 'image' ? id : null]
    const item: Item = { kind: itemKindOf[kind], id }
    // The title field and the remove button in a component's header work as usual.
    const control = e.target instanceof Element && e.target.closest('input, button')
    const several = !readOnly && !control && !kind.endsWith('-resize')
    if (several && adds(e)) {
      e.stopPropagation()
      e.preventDefault()
      setSelected(togglePicked(selected, item))
      return
    }
    const group = several && selected.length > 1 && isSelected(item.kind, id)
    if (!group) setSelection(item)
    if (readOnly || control) return
    beginGesture(e, group ? { kind: 'group', id, handle, items: selected } : { kind, id, handle })
  }

  /** Takes the pointer for a gesture on the canvas, from where it was pressed. */
  function beginGesture(e: PointerEvent, what: Pick<Gesture, 'kind' | 'id' | 'handle' | 'items'>) {
    e.stopPropagation()
    e.preventDefault()
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.card-scroll')) document.activeElement.blur()
    scrollRef.current!.focus({ preventScroll: true })
    canvasRef.current!.setPointerCapture(e.pointerId)
    const point = toCard({ x: e.clientX, y: e.clientY })
    setGesture({ ...what, start: point, current: point, client: { x: e.clientX, y: e.clientY }, outside: false })
  }

  /** With the pen on, a press anywhere on the canvas (over components and images too) starts a stroke. */
  function onPenPointerDown(e: PointerEvent) {
    if (!drawingOn || e.button !== 0) return
    e.stopPropagation()
    e.preventDefault()
    setSelection(null)
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.card-scroll')) document.activeElement.blur()
    scrollRef.current!.focus({ preventScroll: true })
    canvasRef.current!.setPointerCapture(e.pointerId)
    pen.start(e, cardPen.ink, cardPen.width)
  }

  /** Turns the card's pen on or off. */
  function togglePen() {
    setPenOn((on) => !on)
    setSelection(null)
    movePenRing(penRing.current, null)
    scrollRef.current?.focus({ preventScroll: true })
  }

  /** A press on empty canvas draws a selection rectangle (viewers: pans). Presses inside components and images are left to them. */
  function onCanvasPointerDown(e: PointerEvent) {
    const empty = e.target === e.currentTarget
    // The middle button pans, over a component or image too, like on the board, and so does the left one with Space
    // held (without touching the selection or the field being typed in).
    const panning = e.button === 1 || (e.button === 0 && spaceHeld)
    if (!empty && !panning) return
    if (empty) imagePresses.current = [imagePresses.current[1], null]
    if (e.button !== 0 && e.button !== 1) return
    if (empty && !panning && !readOnly) {
      beginGesture(e, { kind: 'select', id: '', handle: 'BottomRight', items: adds(e) ? selected : [] })
      return
    }
    e.preventDefault()
    if (empty && !panning) {
      setSelection(null)
      if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.card-scroll')) document.activeElement.blur()
      scrollRef.current!.focus({ preventScroll: true })
    }
    pan.start(e)
  }

  function onPointerMove(e: PointerEvent) {
    const client = { x: e.clientX, y: e.clientY }
    lastPointer.current = hits(client) ? toCard(client) : null
    if (drawingOn) movePenRing(penRing.current, toCard(client), e.pointerType)
    if (pen.drawing()) {
      pen.extend(e)
    } else if (pan.move(e)) {
      // Scrolled along.
    } else if (gesture && !gesture.done) {
      const frame = scrollRef.current!.closest('.expanded')!.getBoundingClientRect()
      const next = { ...gesture, current: toCard(client), client, outside: !insideClient(frame, client) }
      setGesture(next)
      if (next.kind === 'select' && isDrag(next)) setSelected(mergePicked(next.items ?? [], inRectangle(spanRect(next.start, next.current))))
    }
  }

  function onPointerUp() {
    if (pen.drawing()) {
      pen.finish(run, card.boardId, card.id)
      return
    }
    if (pan.end()) return
    if (!gesture || gesture.done) return
    if (gesture.kind === 'select') {
      // A click without a drag keeps only the selection it was adding to (none without Shift or Ctrl/Cmd).
      setGesture(null)
      if (!isDrag(gesture)) setSelected(gesture.items ?? [])
      return
    }
    if (!isDrag(gesture)) {
      setGesture(null)
      // A click on one of several selected selects just that one.
      const held = gesture.kind === 'group' && gesture.items?.find((i) => i.id === gesture.id)
      if (held) setSelection(held)
      return
    }
    if (gesture.kind === 'group') {
      setGesture({ ...gesture, done: true })
      moveGroup(gesture.items ?? [])
      return
    }
    const { id } = gesture
    if (leaving) {
      // Dropped outside the window: onto the board, held at the point where it was grabbed; elsewhere it goes back.
      const item = gesture.kind === 'drawing' ? drawings.find((d) => d.id === id)! : images.find((i) => i.id === id)!
      const at = boardPoint(gesture.client)
      if (!at) {
        setGesture(null)
        return
      }
      setGesture({ ...gesture, done: true })
      setSelection(null)
      const grab = { x: gesture.start.x - item.x, y: gesture.start.y - item.y }
      const corner = snapPoint({ x: at.x - grab.x, y: at.y - grab.y })
      const place = { detach: true, ...corner, ...dropBucket?.(at, { ...corner, width: item.width, height: item.height }) }
      if (gesture.kind === 'drawing') void settle(...ops.editDrawing(item as DrawingDetail, place, 'Put drawing on the board', ['cardId', 'x', 'y']))
      else void settle(...ops.editImage(item as ImageDetail, place, 'Put image on the board', ['cardId', 'x', 'y']))
      return
    }
    setGesture({ ...gesture, done: true })
    if (gesture.kind === 'drawing') {
      const drawing = drawings.find((d) => d.id === id)!
      const { x, y } = drawingRect(drawing)
      void settle(...ops.editDrawing(drawing, { x, y }, 'Move drawing', ['x', 'y']))
    } else if (gesture.kind === 'component' || gesture.kind === 'component-resize') {
      const component = card.components.find((b) => b.id === id)!
      const { x, y, width, height } = componentRect(component)
      const moving = gesture.kind === 'component'
      const label = moving ? 'Move component' : 'Resize component'
      const keys = ['x', 'y', 'width', 'height']
      void settle(
        () => api.updateCardComponent(card.id, id, moving ? { x, y } : { x, y, width, height }),
        (after) => step(label, [patched(target.component(card.id, id), snap.component(component), snap.component(after), keys)]),
      )
    } else {
      const image = images.find((i) => i.id === id)!
      const { x, y, width } = imageRect(image)
      const moving = gesture.kind === 'image'
      void settle(...ops.editImage(image, moving ? { x, y } : { x, y, width }, moving ? 'Move image' : 'Resize image', ['x', 'y', 'width']))
    }
  }

  const components = card.components.map((component) => ({ component, rect: componentRect(component) }))
  const placed = images.map((image) => ({ image, rect: imageRect(image) }))
  const strokes = drawings.map((drawing) => ({ drawing, rect: drawingRect(drawing) }))
  const ghost = incoming && { ...incoming.image, ...onCardCanvas(incoming.at, incoming.image) }
  const drawingGhost = incomingDrawing && { drawing: incomingDrawing.drawing, at: onCardCanvas(incomingDrawing.at, incomingDrawing.drawing) }
  const all = bounds([
    ...components.map((b) => b.rect),
    ...placed.map((p) => p.rect),
    ...strokes.map((s) => s.rect),
    ...(ghost ? [ghost] : []),
    ...(drawingGhost ? [{ ...drawingGhost.drawing, ...drawingGhost.at }] : []),
  ])
  const extent = {
    width: Math.max(Math.floor(size.width / zoom), CANVAS_MARGIN + Math.max(0, all ? all.x + all.width : 0)),
    height: Math.max(Math.floor(size.height / zoom), CANVAS_MARGIN + Math.max(0, all ? all.y + all.height : 0)),
  }
  const mapComponents = components.map((b) => b.rect)
  const mapImages = placed.map((p) => p.rect)
  const mapDrawings = strokes.map((s) => s.rect)
  const leavingImage = leaving && images.find((i) => i.id === leaving.id)
  const leavingDrawing = leaving && drawings.find((d) => d.id === leaving.id)
  /** The selection rectangle being dragged out. */
  const marquee = gesture?.kind === 'select' && isDrag(gesture) ? spanRect(gesture.start, gesture.current) : null
  const selectedImage = !readOnly && selection?.kind === 'image' ? images.find((i) => i.id === selection.id) : undefined
  const selectedDrawing = !readOnly && selection?.kind === 'drawing' ? strokes.find((s) => s.drawing.id === selection.id) : undefined
  /** A drawing selected by itself scales and turns from its handles (not while drawing). */
  const reshapable = (id: string) => !readOnly && !drawingOn && selected.length === 1 && isSelected('drawing', id)

  return (
    <div className={`card-viewport${incoming || incomingDrawing ? ' drop-over' : ''}`}>
      <div
        ref={scrollRef}
        className={`card-scroll${fileDrop.over ? ' file-over' : ''}`}
        tabIndex={-1}
        aria-label="Card content"
        onKeyDown={(e) => {
          if (readOnly || inField(e.target, { editable: true })) return
          if (e.key === 'Escape' && penOn) {
            // Escape puts the pen down before it closes the card.
            e.preventDefault()
            e.stopPropagation()
            togglePen()
          } else if (e.key.toLowerCase() === 'p' && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault()
            e.stopPropagation()
            togglePen()
          } else if (selected.length > 0 && (e.key === 'Delete' || e.key === 'Backspace')) {
            e.preventDefault()
            e.stopPropagation()
            if (e.repeat || gesture) return
            if (selection) remove(selection.kind, selection.id)
            else removeSelected()
          }
        }}
        {...fileDrop.handlers}
      >
        <div className="canvas-sizer" style={{ width: extent.width * zoom, height: extent.height * zoom }}>
          <div
            ref={canvasRef}
            className={`card-canvas${pan.panning ? ' panning' : spaceHeld ? ' space-pan' : ''}${drawingOn ? ' pen' : ''}`}
            style={{
              width: extent.width,
              height: extent.height,
              transform: `scale(${zoom})`,
              // The canvas holds the pointer during a resize, so it shows the handle's cursor.
              cursor: gesture && !gesture.done && gesture.kind.endsWith('-resize') ? resizeCursor[gesture.handle] : undefined,
            }}
            onPointerDownCapture={onPenPointerDown}
            onPointerDown={onCanvasPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={onPointerUp}
            onPointerLeave={() => movePenRing(penRing.current, null)}
            onPointerCancel={() => {
              setGesture(null)
              pan.end()
              pen.cancel()
            }}
            // Losing the pointer without a release (another window took it) ends a pan or an unfinished drag.
            onLostPointerCapture={() => {
              pan.end()
              setGesture((g) => (g && !g.done ? null : g))
              pen.cancel()
            }}
            onDoubleClick={() => {
              const [first, second] = imagePresses.current
              const image = second && first === second && images.find((i) => i.id === second)
              if (image) window.open(image.contentUrl, '_blank', 'noopener')
            }}
          >
            {placed.map(({ image, rect }) => (
              <div key={image.id} className={leavingImage?.id === image.id ? 'leaving' : undefined}>
                <BoardImageView
                  image={image}
                  rect={rect}
                  selected={isSelected('image', image.id)}
                  lifted={moves(image.id) && !dragging?.done}
                  onPointerDown={(e) => onItemPointerDown(e, 'image', image.id)}
                  // One of several selected has no handles: they move together, but resize one at a time.
                  onResizePointerDown={
                    readOnly || selection?.id !== image.id ? undefined : (e, handle) => onItemPointerDown(e, 'image-resize', image.id, handle)
                  }
                  onDelete={readOnly ? undefined : () => remove('image', image.id)}
                />
              </div>
            ))}

            {components.map(({ component, rect }) => (
              <ComponentFrame
                key={component.id}
                component={component}
                rect={rect}
                selected={isSelected('component', component.id)}
                lifted={moves(component.id)}
                matched={!!highlight && componentMatches(component, highlight)}
                readOnly={readOnly}
                onHeadPointerDown={(e) => onItemPointerDown(e, 'component', component.id)}
                onResizePointerDown={selection?.id === component.id ? (e, handle) => onItemPointerDown(e, 'component-resize', component.id, handle) : undefined}
                onSelect={() => setSelection({ kind: 'component', id: component.id })}
                onTitle={(title) => void saveComponent(component, { title })}
                onRemove={() => remove('component', component.id)}
              >
                {component.kind === 'Text' && (
                  <TextComponent
                    text={component.text ?? ''}
                    autoFocus={focusComponent === component.id}
                    readOnly={readOnly}
                    onChange={(text) => void saveComponent(component, { text })}
                  />
                )}
                {component.kind === 'Checklist' && (
                  <ChecklistComponent
                    items={component.items ?? []}
                    highlight={highlight}
                    autoFocus={focusComponent === component.id}
                    readOnly={readOnly}
                    onChange={(items) => saveComponent(component, { items })}
                  />
                )}
                {component.kind === 'Links' && (
                  <LinksComponent
                    links={component.links ?? []}
                    highlight={highlight}
                    card={card}
                    cards={cards}
                    autoFocus={focusComponent === component.id}
                    onChange={(links) => saveComponent(component, { links })}
                  />
                )}
              </ComponentFrame>
            ))}

            {strokes.map(({ drawing, rect }) => (
              <div key={drawing.id} className={leavingDrawing?.id === drawing.id ? 'leaving' : undefined}>
                <DrawingView
                  drawing={drawing}
                  rect={rect}
                  selected={isSelected('drawing', drawing.id)}
                  lifted={moves(drawing.id) && !dragging?.done}
                  onPointerDown={(e) => onItemPointerDown(e as PointerEvent<HTMLElement>, 'drawing', drawing.id)}
                  zoom={zoom}
                  onReshape={reshapable(drawing.id) ? (reshape) => settle(...ops.reshapeDrawing(drawing, reshape)) : undefined}
                />
              </div>
            ))}

            {pen.strokes.map((s, i) => (
              <StrokeSvg key={i} points={s.points} color={inkColor[s.color]} strokeWidth={s.width} at={{ x: 0, y: 0 }} />
            ))}

            {selectedDrawing && !(dragging?.id === selectedDrawing.drawing.id && !dragging.done) && (
              <DrawingToolbar
                drawing={selectedDrawing.drawing}
                rect={selectedDrawing.rect}
                zoom={zoom}
                onRecolor={(color) => recolorDrawing(selectedDrawing.drawing, color)}
                onDelete={() => remove('drawing', selectedDrawing.drawing.id)}
              />
            )}

            {ghost && (
              <div className="incoming-image" aria-hidden="true" style={{ left: ghost.x, top: ghost.y, width: ghost.width, height: ghost.height }}>
                <img src={ghost.contentUrl} alt="" draggable={false} />
              </div>
            )}

            {drawingGhost && <IncomingDrawingView drawing={drawingGhost.drawing} at={drawingGhost.at} />}

            {drawingOn && <PenRing ringRef={penRing} width={cardPen.width} zoom={zoom} />}

            {marquee && (
              <div className="marquee" aria-hidden="true" style={{ left: marquee.x, top: marquee.y, width: marquee.width, height: marquee.height }} />
            )}
          </div>
        </div>
      </div>

      {card.components.length === 0 && images.length === 0 && drawings.length === 0 && !incoming && !incomingDrawing && !drawingOn && (
        <p className="card-empty">
          {readOnly ? 'Nothing here yet.' : 'Add a note, a checklist or links from the bar below, draw with the pen, or paste or drop an image.'}
        </p>
      )}

      <ViewControls
        className="card-view-controls"
        undo={history?.undo}
        redo={history?.redo}
        zoom={zoom}
        onZoom={(direction) => zoomTo(stepZoom(zoom, direction))}
        onZoomReset={() => zoomTo(1)}
        extent={{ x: 0, y: 0, ...extent }}
        view={view}
        viewport={viewport}
        items={(left, top, scale) => (
          <CardMapItems left={left} top={top} scale={scale} components={mapComponents} images={mapImages} drawings={mapDrawings} />
        )}
        onNavigate={navigate}
      />

      {!readOnly && (
        <CardBar
          onAdd={addComponent}
          onAddImage={onPickImages}
          pen={{
            on: penOn,
            onToggle: togglePen,
            ink: cardPen.ink,
            onInkChange: cardPen.setInk,
            width: cardPen.width,
            onWidthChange: cardPen.setWidth,
          }}
          onDelete={onDeleteCard}
        />
      )}

      {selectedImage && (
        <ImageInspector
          key={`${selectedImage.id}:${selectedImage.caption}`}
          image={selectedImage}
          onSave={(changes) => settle(...ops.editImage(selectedImage, changes, 'Edit caption', ['caption']))}
          onDelete={() => remove('image', selectedImage.id)}
          onClose={() => setSelection(null)}
        />
      )}

      {leaving &&
        leavingImage &&
        createPortal(
          <div
            className="drag-ghost"
            aria-hidden="true"
            style={{
              left: leaving.client.x - (leaving.start.x - leavingImage.x) * boardZoom,
              top: leaving.client.y - (leaving.start.y - leavingImage.y) * boardZoom,
              width: leavingImage.width * boardZoom,
              height: leavingImage.height * boardZoom,
            }}
          >
            <img src={leavingImage.contentUrl} alt="" draggable={false} />
          </div>,
          document.body,
        )}

      {/* A drawing dragged out of the window, at the board's zoom, held where it was grabbed. */}
      {leaving &&
        leavingDrawing &&
        createPortal(
          <DrawingGhost
            drawing={leavingDrawing}
            at={{
              x: leaving.client.x - (leaving.start.x - leavingDrawing.x) * boardZoom,
              y: leaving.client.y - (leaving.start.y - leavingDrawing.y) * boardZoom,
            }}
            zoom={boardZoom}
          />,
          document.body,
        )}
    </div>
  )
}
