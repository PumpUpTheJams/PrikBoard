import { useEffect, useImperativeHandle, useLayoutEffect, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../api/client'
import type { DrawingDetail, ImageDetail, InkColor, NoteBlock, NoteBlockKind, NoteDetail, Point, Rect, ResizeHandle } from '../api/types'
import { BoardImageView } from './BoardImageView'
import { inkHex, movePenRing, NOTE_PEN, usePen } from './drawings'
import { DrawingGhost, DrawingToolbar, DrawingView, IncomingDrawingView, PenRing, StrokeSvg } from './DrawingView'
import { draggingFiles, imageFiles } from './files'
import { delta, isDrag } from './gesture'
import { MinusIcon, PlusIcon } from './icons'
import { ImageInspector } from './Inspector'
import { blockSize, defaultBlockSize, freeSpot, onNoteCanvas, resizeFrom, resizeImage, snapPoint } from './layout'
import { BlockFrame, ChecklistBlock, LinksBlock, TextBlock } from './NoteBlocks'
import { NoteBar } from './NoteBar'
import { resizeCursor } from './resizeCursor'
import { blockMatches } from './search'
import { blockEdit, created, deleted, patched, snap, step, target, type RecordingRun, type UndoEntry } from './undo'
import { useStroke } from './useStroke'
import { useZoom } from './useZoom'
import { stepZoom, ZOOM_LEVELS } from './zoom'

/** What the board needs from the post-it's canvas to drop images into it and paste there. */
export interface NoteCanvasHandle {
  /** Whether a screen point is over the visible part of the canvas. */
  hits: (client: Point) => boolean
  /** A screen point in note pixels. */
  toNote: (client: Point) => Point
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

type Kind = 'block' | 'block-resize' | 'image' | 'image-resize' | 'drawing'

/**
 * A press on a block or image, in note pixels, with the last screen point and whether it is outside the window (an
 * image dragged out, to drop on the board). A finished gesture (`done`) stays until the board has reloaded, so the item
 * doesn't jump back meanwhile.
 */
interface Gesture {
  kind: Kind
  id: string
  /** For resizes: the corner or edge being dragged. */
  handle: ResizeHandle
  start: Point
  current: Point
  client: Point
  outside: boolean
  done?: boolean
}

interface Pan {
  x: number
  y: number
  left: number
  top: number
}

const NO_INSET = { x: 0, y: 0 }

/** Empty canvas kept beyond the furthest block or image: one grid step, so scroll bars only come when something sticks out. */
const CANVAS_MARGIN = 20

interface NoteCanvasProps {
  note: NoteDetail
  /** Images attached to the post-it. */
  images: ImageDetail[]
  /** Pen strokes on the post-it. */
  drawings: DrawingDetail[]
  /** The post-its on the board, to link to. */
  notes: NoteDetail[]
  readOnly?: boolean
  /** Search words to point out: blocks they occur in get an outline, and checklists and links mark them. */
  highlight?: string[]
  handle: RefObject<NoteCanvasHandle | null>
  /** Runs a change through the board (which then reloads), recorded for undo. */
  run: RecordingRun
  /** Whether a gesture is in progress, so the board holds back its refreshes. */
  onBusy: (busy: boolean) => void
  incoming: IncomingImage | null
  incomingDrawing: IncomingDrawing | null
  onAddImages: (files: File[], at?: Point) => void
  onPickImages: () => void
  /** A screen point on the board (canvas pixels) when it is over the board, to drop an image there; else null. */
  boardPoint: (client: Point) => Point | null
  boardZoom: number
  /** Asks to delete the whole post-it. */
  onDeletePostIt: () => void
}

/** The post-it's own small board: its blocks and images, moved and resized like items on the board, with zoom and pan. */
export function NoteCanvas(props: NoteCanvasProps) {
  const { note, images, drawings, notes, readOnly, highlight, handle, run, onBusy, incoming, incomingDrawing, onAddImages, onPickImages, boardPoint, boardZoom, onDeletePostIt } = props
  const [selection, setSelection] = useState<{ kind: 'block' | 'image' | 'drawing'; id: string } | null>(null)
  const [gesture, setGesture] = useState<Gesture | null>(null)
  const [pan, setPan] = useState<Pan | null>(null)
  /** The block added last: its main field is focused when it appears. */
  const [focusBlock, setFocusBlock] = useState<string | null>(null)
  const [fileOver, setFileOver] = useState(false)
  /** The scroll viewport in screen pixels, for the canvas size and where new blocks go. */
  const [view, setView] = useState<Rect>({ x: 0, y: 0, width: 0, height: 0 })
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  const lastPointer = useRef<Point | null>(null)
  /** The images the last two presses were on: the canvas holds the pointer, so double-clicks reach it, not the image. */
  const imagePresses = useRef<[string | null, string | null]>([null, null])
  const { zoom, zoomTo } = useZoom(scrollRef, NO_INSET)
  /** The post-it's pen: on or off (a press then draws instead of panning), with an ink and size of its own. */
  const [penOn, setPenOn] = useState(false)
  const notePen = usePen(NOTE_PEN)
  const penRing = useRef<HTMLDivElement>(null)
  const drawingOn = !readOnly && penOn
  useEffect(() => () => onBusy(false), [onBusy])

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

  function toNote(client: Point): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: (client.x - rect.left) / zoom, y: (client.y - rect.top) / zoom }
  }

  /** The stroke being drawn with the pen. */
  const pen = useStroke((e) => toNote({ x: e.clientX, y: e.clientY }))
  useEffect(() => onBusy(gesture !== null || pen.stroke !== null), [gesture, pen.stroke, onBusy])

  const contains = (rect: DOMRect, p: Point) => p.x >= rect.left && p.x < rect.right && p.y >= rect.top && p.y < rect.bottom
  const hits = (client: Point) => !!scrollRef.current && contains(scrollRef.current.getBoundingClientRect(), client)
  /** What is visible of the canvas, in note pixels. */
  const visible = (): Rect => ({ x: view.x / zoom, y: view.y / zoom, width: view.width / zoom, height: view.height / zoom })

  useImperativeHandle(handle, () => ({
    hits,
    toNote,
    pastePoint: () => {
      const v = visible()
      return lastPointer.current ?? { x: v.x + v.width / 2, y: v.y + v.height / 2 }
    },
  }))

  async function settle<T>(action: () => Promise<T>, describe?: (result: T) => UndoEntry | null | undefined) {
    const ok = await run(action, describe)
    setGesture(null)
    return ok
  }

  const saveBlock = (block: NoteBlock, changes: Parameters<typeof api.updateNoteBlock>[2]) =>
    settle(() => api.updateNoteBlock(note.id, block.id, changes), (after) => blockEdit(note.id, block, after, changes))

  /** The undo step for an image on this canvas that moved, was resized or went onto the board. */
  const imageStep = (label: string, before: ImageDetail, keys: string[]) => (after: ImageDetail) =>
    step(label, [patched(target.image(before.id), snap.image(before), snap.image(after), keys)])

  /** The undo step for a drawing on this canvas that moved, was recoloured or went onto the board. */
  const drawingStep = (label: string, before: DrawingDetail, keys: string[]) => (after: DrawingDetail) =>
    step(label, [patched(target.drawing(before.id), snap.drawing(before), snap.drawing(after), keys)])

  function recolorDrawing(drawing: DrawingDetail, color: InkColor) {
    void settle(() => api.updateDrawing(drawing.id, { color }), drawingStep('Change ink', drawing, ['color']))
  }

  /** Adds a block in the first free spot in view (or, with none, wherever the server finds room) and focuses it. */
  function addBlock(kind: NoteBlockKind) {
    const size = defaultBlockSize(kind)
    const spot = freeSpot([...note.blocks, ...images], size, visible())
    void settle(
      async () => {
        const block = await api.addNoteBlock(note.id, { kind, ...(spot ?? {}) })
        setFocusBlock(block.id)
        setSelection({ kind: 'block', id: block.id })
        return block
      },
      (block) => step('Add block', [created(target.block(note.id, block.id), snap.block(block))]),
    )
  }

  /** Removes a block, image or drawing straight away; the toast that follows can undo it (and so can Ctrl/Cmd+Z). */
  function remove(kind: 'block' | 'image' | 'drawing', id: string) {
    if (selection?.id === id) setSelection(null)
    const block = kind === 'block' ? note.blocks.find((b) => b.id === id) : undefined
    const image = kind === 'image' ? images.find((i) => i.id === id) : undefined
    const drawing = kind === 'drawing' ? drawings.find((d) => d.id === id) : undefined
    if (block) {
      // An empty block goes quietly; it can still be undone.
      const empty = !block.text && !block.items?.length && !block.links?.length && !block.title
      void settle(
        () => api.deleteNoteBlock(note.id, id),
        () => step('Remove block', [deleted(target.block(note.id, id), snap.block(block))], { toast: empty ? undefined : 'Block removed' }),
      )
    } else if (image) {
      void settle(
        () => api.deleteImage(id),
        () => step('Delete image', [deleted(target.image(id), snap.image(image))], { toast: 'Image deleted' }),
      )
    } else if (drawing) {
      void settle(
        () => api.deleteDrawing(id),
        () => step('Delete drawing', [deleted(target.drawing(id), snap.drawing(drawing))], { toast: 'Drawing deleted' }),
      )
    }
  }

  // Live geometry: the server's layout with your gesture applied, snapped as the server will.
  const dragging = gesture && isDrag(gesture) ? gesture : null
  /** An image or drawing dragged out of the window, to drop on the board. */
  const leaving = (dragging?.kind === 'image' || dragging?.kind === 'drawing') && !dragging.done && dragging.outside ? dragging : null

  function blockRect(block: NoteBlock): Rect {
    if (dragging?.id !== block.id) return block
    const d = delta(dragging)
    if (dragging.kind === 'block') return { ...block, ...onNoteCanvas({ x: block.x + d.x, y: block.y + d.y }, block) }
    const resized = resizeFrom(block, dragging.handle, d, blockSize)
    return { ...block, ...resized, ...onNoteCanvas(resized, resized) }
  }

  function imageRect(image: ImageDetail): Rect {
    if (dragging?.id !== image.id || leaving) return image
    const d = delta(dragging)
    if (dragging.kind === 'image') return { ...image, ...onNoteCanvas({ x: image.x + d.x, y: image.y + d.y }, image) }
    const resized = resizeImage(image, dragging.handle, d)
    return { ...image, ...resized, ...onNoteCanvas(resized, resized) }
  }

  function drawingRect(drawing: DrawingDetail): Rect {
    if (dragging?.id !== drawing.id || leaving) return drawing
    const d = delta(dragging)
    return { ...drawing, ...onNoteCanvas({ x: drawing.x + d.x, y: drawing.y + d.y }, drawing) }
  }

  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: Kind, id: string, handle: ResizeHandle = 'BottomRight') {
    // With the pen on, a press draws over whatever is there (onPenPointerDown took it already).
    if (e.button !== 0 || drawingOn) return
    imagePresses.current = [imagePresses.current[1], kind === 'image' ? id : null]
    setSelection({ kind: kind === 'block' || kind === 'block-resize' ? 'block' : kind === 'drawing' ? 'drawing' : 'image', id })
    // The title field and the remove button in a block's header work as usual.
    if (readOnly || (e.target instanceof Element && e.target.closest('input, button'))) return
    e.stopPropagation()
    e.preventDefault()
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.note-scroll')) document.activeElement.blur()
    scrollRef.current!.focus({ preventScroll: true })
    canvasRef.current!.setPointerCapture(e.pointerId)
    const point = toNote({ x: e.clientX, y: e.clientY })
    setGesture({ kind, id, handle, start: point, current: point, client: { x: e.clientX, y: e.clientY }, outside: false })
  }

  /** With the pen on, a press anywhere on the canvas (over blocks and images too) starts a stroke. */
  function onPenPointerDown(e: PointerEvent) {
    if (!drawingOn || e.button !== 0) return
    e.stopPropagation()
    e.preventDefault()
    setSelection(null)
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.note-scroll')) document.activeElement.blur()
    scrollRef.current!.focus({ preventScroll: true })
    canvasRef.current!.setPointerCapture(e.pointerId)
    pen.start(e, notePen.ink, notePen.width)
  }

  /** Turns the post-it's pen on or off. */
  function togglePen() {
    setPenOn((on) => !on)
    setSelection(null)
    movePenRing(penRing.current, null)
    scrollRef.current?.focus({ preventScroll: true })
  }

  /**
   * A press on empty canvas pans, and so does the middle button over a block or image, like on the board (without
   * touching the selection or the field being typed in). Other presses inside blocks and images are theirs.
   */
  function onCanvasPointerDown(e: PointerEvent) {
    const empty = e.target === e.currentTarget
    if (!empty && e.button !== 1) return
    if (empty) imagePresses.current = [imagePresses.current[1], null]
    if (e.button !== 0 && e.button !== 1) return
    e.preventDefault()
    if (empty) {
      setSelection(null)
      if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.note-scroll')) document.activeElement.blur()
      scrollRef.current!.focus({ preventScroll: true })
    }
    const el = scrollRef.current!
    canvasRef.current!.setPointerCapture(e.pointerId)
    setPan({ x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop })
  }

  function onPointerMove(e: PointerEvent) {
    const client = { x: e.clientX, y: e.clientY }
    lastPointer.current = hits(client) ? toNote(client) : null
    if (drawingOn) movePenRing(penRing.current, toNote(client), e.pointerType)
    if (pen.drawing()) {
      pen.extend(e)
    } else if (pan) {
      const el = scrollRef.current!
      el.scrollLeft = pan.left - (e.clientX - pan.x)
      el.scrollTop = pan.top - (e.clientY - pan.y)
    } else if (gesture && !gesture.done) {
      const frame = scrollRef.current!.closest('.expanded')!.getBoundingClientRect()
      setGesture({ ...gesture, current: toNote(client), client, outside: !contains(frame, client) })
    }
  }

  function onPointerUp() {
    if (pen.drawing()) {
      pen.finish(run, note.boardId, note.id)
      return
    }
    if (pan) {
      setPan(null)
      return
    }
    if (!gesture || gesture.done) return
    if (!isDrag(gesture)) {
      setGesture(null)
      return
    }
    const { id } = gesture
    if (leaving) {
      // Dropped outside the window: onto the board, at the same grab point, or back where it was.
      const item = gesture.kind === 'drawing' ? drawings.find((d) => d.id === id)! : images.find((i) => i.id === id)!
      const at = boardPoint(gesture.client)
      if (!at) {
        setGesture(null)
        return
      }
      setGesture({ ...gesture, done: true })
      setSelection(null)
      const grab = { x: gesture.start.x - item.x, y: gesture.start.y - item.y }
      const place = { detach: true, ...snapPoint({ x: at.x - grab.x, y: at.y - grab.y }) }
      if (gesture.kind === 'drawing') {
        const drawing = item as DrawingDetail
        void settle(() => api.updateDrawing(id, place), drawingStep('Put drawing on the board', drawing, ['noteId', 'x', 'y']))
      } else {
        void settle(() => api.updateImage(id, place), imageStep('Put image on the board', item as ImageDetail, ['noteId', 'x', 'y']))
      }
      return
    }
    setGesture({ ...gesture, done: true })
    if (gesture.kind === 'drawing') {
      const drawing = drawings.find((d) => d.id === id)!
      const { x, y } = drawingRect(drawing)
      void settle(() => api.updateDrawing(id, { x, y }), drawingStep('Move drawing', drawing, ['x', 'y']))
    } else if (gesture.kind === 'block' || gesture.kind === 'block-resize') {
      const block = note.blocks.find((b) => b.id === id)!
      const { x, y, width, height } = blockRect(block)
      const moving = gesture.kind === 'block'
      void settle(
        () => api.updateNoteBlock(note.id, id, moving ? { x, y } : { x, y, width, height }),
        (after) => step(moving ? 'Move block' : 'Resize block', [patched(target.block(note.id, id), snap.block(block), snap.block(after), ['x', 'y', 'width', 'height'])]),
      )
    } else {
      const image = images.find((i) => i.id === id)!
      const { x, y, width } = imageRect(image)
      const moving = gesture.kind === 'image'
      void settle(() => api.updateImage(id, moving ? { x, y } : { x, y, width }), imageStep(moving ? 'Move image' : 'Resize image', image, ['x', 'y', 'width']))
    }
  }

  const blocks = note.blocks.map((block) => ({ block, rect: blockRect(block) }))
  const placed = images.map((image) => ({ image, rect: imageRect(image) }))
  const strokes = drawings.map((drawing) => ({ drawing, rect: drawingRect(drawing) }))
  const ghost = incoming && { ...incoming.image, ...onNoteCanvas(incoming.at, incoming.image) }
  const drawingGhost = incomingDrawing && { drawing: incomingDrawing.drawing, at: onNoteCanvas(incomingDrawing.at, incomingDrawing.drawing) }
  const items: Rect[] = [
    ...blocks.map((b) => b.rect),
    ...placed.map((p) => p.rect),
    ...strokes.map((s) => s.rect),
    ...(ghost ? [ghost] : []),
    ...(drawingGhost ? [{ ...drawingGhost.drawing, ...drawingGhost.at }] : []),
  ]
  const extent = {
    width: Math.max(Math.floor(view.width / zoom), CANVAS_MARGIN + Math.max(0, ...items.map((r) => r.x + r.width))),
    height: Math.max(Math.floor(view.height / zoom), CANVAS_MARGIN + Math.max(0, ...items.map((r) => r.y + r.height))),
  }
  const leavingImage = leaving && images.find((i) => i.id === leaving.id)
  const leavingDrawing = leaving && drawings.find((d) => d.id === leaving.id)
  const selectedImage = !readOnly && selection?.kind === 'image' ? images.find((i) => i.id === selection.id) : undefined
  const selectedDrawing = !readOnly && selection?.kind === 'drawing' ? strokes.find((s) => s.drawing.id === selection.id) : undefined

  return (
    <div className={`note-viewport${incoming || incomingDrawing ? ' drop-over' : ''}`}>
      <div
        ref={scrollRef}
        className={`note-scroll${fileOver ? ' file-over' : ''}`}
        tabIndex={-1}
        aria-label="Post-it content"
        onKeyDown={(e) => {
          if (readOnly || (e.target instanceof Element && e.target.closest('input, textarea, [contenteditable="true"]'))) return
          if (e.key === 'Escape' && penOn) {
            // Escape puts the pen down before it closes the post-it.
            e.preventDefault()
            e.stopPropagation()
            togglePen()
          } else if (e.key.toLowerCase() === 'p' && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault()
            e.stopPropagation()
            togglePen()
          } else if (selection && (e.key === 'Delete' || e.key === 'Backspace')) {
            e.preventDefault()
            e.stopPropagation()
            if (!e.repeat) remove(selection.kind, selection.id)
          }
        }}
        // Dropped files are attached where they land; files over the band are handled by the window.
        onDragOver={(e) => {
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          e.stopPropagation()
          e.dataTransfer.dropEffect = 'copy'
          setFileOver(true)
        }}
        onDragLeave={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFileOver(false)
        }}
        onDrop={(e) => {
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          e.stopPropagation()
          setFileOver(false)
          onAddImages(imageFiles(e.dataTransfer), toNote({ x: e.clientX, y: e.clientY }))
        }}
      >
        <div className="canvas-sizer" style={{ width: extent.width * zoom, height: extent.height * zoom }}>
          <div
            ref={canvasRef}
            className={`note-canvas${pan ? ' panning' : ''}${drawingOn ? ' pen' : ''}`}
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
              setPan(null)
              pen.cancel()
            }}
            // Losing the pointer without a release (another window took it) ends a pan or an unfinished drag.
            onLostPointerCapture={() => {
              setPan(null)
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
                  selected={selection?.kind === 'image' && selection.id === image.id}
                  lifted={dragging?.id === image.id && !dragging.done}
                  onPointerDown={(e) => onItemPointerDown(e, 'image', image.id)}
                  onResizePointerDown={readOnly ? undefined : (e, handle) => onItemPointerDown(e, 'image-resize', image.id, handle)}
                  onDelete={readOnly ? undefined : () => remove('image', image.id)}
                />
              </div>
            ))}

            {blocks.map(({ block, rect }) => (
              <BlockFrame
                key={block.id}
                block={block}
                rect={rect}
                selected={selection?.kind === 'block' && selection.id === block.id}
                lifted={dragging?.id === block.id}
                matched={!!highlight && blockMatches(block, highlight)}
                readOnly={readOnly}
                onHeadPointerDown={(e) => onItemPointerDown(e, 'block', block.id)}
                onResizePointerDown={(e, handle) => onItemPointerDown(e, 'block-resize', block.id, handle)}
                onSelect={() => setSelection({ kind: 'block', id: block.id })}
                onTitle={(title) => void saveBlock(block, { title })}
                onRemove={() => remove('block', block.id)}
              >
                {block.kind === 'Text' && (
                  <TextBlock
                    text={block.text ?? ''}
                    autoFocus={focusBlock === block.id}
                    readOnly={readOnly}
                    onChange={(text) => void saveBlock(block, { text })}
                  />
                )}
                {block.kind === 'Checklist' && (
                  <ChecklistBlock
                    items={block.items ?? []}
                    highlight={highlight}
                    autoFocus={focusBlock === block.id}
                    readOnly={readOnly}
                    onChange={(items) => saveBlock(block, { items })}
                  />
                )}
                {block.kind === 'Links' && (
                  <LinksBlock
                    links={block.links ?? []}
                    highlight={highlight}
                    note={note}
                    notes={notes}
                    autoFocus={focusBlock === block.id}
                    onChange={(links) => saveBlock(block, { links })}
                  />
                )}
              </BlockFrame>
            ))}

            {strokes.map(({ drawing, rect }) => (
              <div key={drawing.id} className={leavingDrawing?.id === drawing.id ? 'leaving' : undefined}>
                <DrawingView
                  drawing={drawing}
                  rect={rect}
                  selected={selection?.kind === 'drawing' && selection.id === drawing.id}
                  lifted={dragging?.id === drawing.id && !dragging.done}
                  onPointerDown={(e) => onItemPointerDown(e as PointerEvent<HTMLElement>, 'drawing', drawing.id)}
                />
              </div>
            ))}

            {pen.stroke && <StrokeSvg points={pen.stroke.points} color={inkHex[pen.stroke.color]} strokeWidth={pen.stroke.width} at={{ x: 0, y: 0 }} />}

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

            {drawingOn && <PenRing ringRef={penRing} width={notePen.width} zoom={zoom} />}
          </div>
        </div>
      </div>

      {note.blocks.length === 0 && images.length === 0 && drawings.length === 0 && !incoming && !incomingDrawing && !drawingOn && (
        <p className="note-empty">
          {readOnly ? 'Nothing here yet.' : 'Add a note, a checklist or links from the bar below, draw with the pen, or paste or drop an image.'}
        </p>
      )}

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

      {!readOnly && (
        <NoteBar
          onAdd={addBlock}
          onAddImage={onPickImages}
          pen={{
            on: penOn,
            onToggle: togglePen,
            ink: notePen.ink,
            onInkChange: notePen.setInk,
            width: notePen.width,
            onWidthChange: notePen.setWidth,
          }}
          onDelete={onDeletePostIt}
        />
      )}

      {selectedImage && (
        <ImageInspector
          key={`${selectedImage.id}:${selectedImage.caption}`}
          image={selectedImage}
          onSave={(changes) => settle(() => api.updateImage(selectedImage.id, changes), imageStep('Edit caption', selectedImage, ['caption']))}
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
