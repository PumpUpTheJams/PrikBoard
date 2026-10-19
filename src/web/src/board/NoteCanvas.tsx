import { useEffect, useImperativeHandle, useLayoutEffect, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../api/client'
import type { ImageDetail, NoteBlock, NoteBlockKind, NoteDetail, Point, Rect } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { BoardImageView } from './BoardImageView'
import { draggingFiles, imageFiles } from './files'
import { delta, isDrag } from './gesture'
import { MinusIcon, PlusIcon } from './icons'
import { ImageInspector } from './Inspector'
import { blockSize, defaultBlockSize, freeSpot, imageSize, onNoteCanvas, snapPoint } from './layout'
import { blockKinds } from './blockKinds'
import { BlockFrame, ChecklistBlock, LinksBlock, TextBlock } from './NoteBlocks'
import { NoteBar } from './NoteBar'
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

type Kind = 'block' | 'block-resize' | 'image' | 'image-resize'

/**
 * A press on a block or image, in note pixels, with the last screen point and whether it is outside the window (an
 * image dragged out, to drop on the board). A finished gesture (`done`) stays until the board has reloaded, so the item
 * doesn't jump back meanwhile.
 */
interface Gesture {
  kind: Kind
  id: string
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

/** Empty canvas kept beyond the furthest block or image: one grid step, so scroll bars only come when something sticks out. */
const CANVAS_MARGIN = 20

interface NoteCanvasProps {
  note: NoteDetail
  /** Images attached to the post-it. */
  images: ImageDetail[]
  /** The post-its on the board, to link to. */
  notes: NoteDetail[]
  readOnly?: boolean
  handle: RefObject<NoteCanvasHandle | null>
  /** Runs a change through the board (which then reloads). */
  run: (action: () => Promise<unknown>) => Promise<boolean>
  /** Whether a gesture is in progress, so the board holds back its refreshes. */
  onBusy: (busy: boolean) => void
  incoming: IncomingImage | null
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
  const { note, images, notes, readOnly, handle, run, onBusy, incoming, onAddImages, onPickImages, boardPoint, boardZoom, onDeletePostIt } = props
  const [selection, setSelection] = useState<{ kind: 'block' | 'image'; id: string } | null>(null)
  const [gesture, setGesture] = useState<Gesture | null>(null)
  const [pan, setPan] = useState<Pan | null>(null)
  /** The block added last: its main field is focused when it appears. */
  const [focusBlock, setFocusBlock] = useState<string | null>(null)
  const [pendingRemove, setPendingRemove] = useState<{ kind: 'block' | 'image'; id: string } | null>(null)
  const [fileOver, setFileOver] = useState(false)
  /** The scroll viewport in screen pixels, for the canvas size and where new blocks go. */
  const [view, setView] = useState<Rect>({ x: 0, y: 0, width: 0, height: 0 })
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  const lastPointer = useRef<Point | null>(null)
  /** The images the last two presses were on: the canvas holds the pointer, so double-clicks reach it, not the image. */
  const imagePresses = useRef<[string | null, string | null]>([null, null])
  const { zoom, zoomTo } = useZoom(scrollRef, 0)

  useEffect(() => onBusy(gesture !== null), [gesture, onBusy])
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

  async function settle(action: () => Promise<unknown>) {
    const ok = await run(action)
    setGesture(null)
    return ok
  }

  const saveBlock = (block: NoteBlock, changes: Parameters<typeof api.updateNoteBlock>[2]) =>
    settle(() => api.updateNoteBlock(note.id, block.id, changes))

  /** Adds a block in the first free spot in view (or, with none, wherever the server finds room) and focuses it. */
  function addBlock(kind: NoteBlockKind) {
    const size = defaultBlockSize(kind)
    const spot = freeSpot([...note.blocks, ...images], size, visible())
    void settle(async () => {
      const block = await api.addNoteBlock(note.id, { kind, ...(spot ?? {}) })
      setFocusBlock(block.id)
      setSelection({ kind: 'block', id: block.id })
    })
  }

  /** Removes a block or image, asking first unless it is an empty block. */
  function askRemove(kind: 'block' | 'image', id: string) {
    const block = kind === 'block' ? note.blocks.find((b) => b.id === id) : undefined
    if (block && !block.text && !block.items?.length && !block.links?.length && !block.title) remove(kind, id)
    else setPendingRemove({ kind, id })
  }

  function remove(kind: 'block' | 'image', id: string) {
    setPendingRemove(null)
    if (selection?.id === id) setSelection(null)
    void settle(() => (kind === 'block' ? api.deleteNoteBlock(note.id, id) : api.deleteImage(id)))
  }

  // Live geometry: the server's layout with your gesture applied, snapped as the server will.
  const dragging = gesture && isDrag(gesture) ? gesture : null
  /** An image dragged out of the window, to drop on the board. */
  const leaving = dragging?.kind === 'image' && !dragging.done && dragging.outside ? dragging : null

  function blockRect(block: NoteBlock): Rect {
    if (dragging?.id !== block.id) return block
    const d = delta(dragging)
    if (dragging.kind === 'block') return { ...block, ...onNoteCanvas({ x: block.x + d.x, y: block.y + d.y }, block) }
    return { ...block, ...blockSize(block.width + d.x, block.height + d.y) }
  }

  function imageRect(image: ImageDetail): Rect {
    if (dragging?.id !== image.id || leaving) return image
    const d = delta(dragging)
    if (dragging.kind === 'image') return { ...image, ...onNoteCanvas({ x: image.x + d.x, y: image.y + d.y }, image) }
    return { ...image, ...imageSize(image, image.width + d.x) }
  }

  function onItemPointerDown(e: PointerEvent<HTMLElement>, kind: Kind, id: string) {
    if (e.button !== 0) return
    imagePresses.current = [imagePresses.current[1], kind === 'image' ? id : null]
    setSelection({ kind: kind === 'block' || kind === 'block-resize' ? 'block' : 'image', id })
    // The title field and the remove button in a block's header work as usual.
    if (readOnly || (e.target instanceof Element && e.target.closest('input, button:not(.block-resize):not(.resize-handle)'))) return
    e.stopPropagation()
    e.preventDefault()
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.note-scroll')) document.activeElement.blur()
    scrollRef.current!.focus({ preventScroll: true })
    canvasRef.current!.setPointerCapture(e.pointerId)
    const point = toNote({ x: e.clientX, y: e.clientY })
    setGesture({ kind, id, start: point, current: point, client: { x: e.clientX, y: e.clientY }, outside: false })
  }

  /** A press on empty canvas pans (presses inside blocks and images are theirs). */
  function onCanvasPointerDown(e: PointerEvent) {
    if (e.target !== e.currentTarget) return
    imagePresses.current = [imagePresses.current[1], null]
    if (e.button !== 0 && e.button !== 1) return
    e.preventDefault()
    setSelection(null)
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest('.note-scroll')) document.activeElement.blur()
    scrollRef.current!.focus({ preventScroll: true })
    const el = scrollRef.current!
    canvasRef.current!.setPointerCapture(e.pointerId)
    setPan({ x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop })
  }

  function onPointerMove(e: PointerEvent) {
    const client = { x: e.clientX, y: e.clientY }
    lastPointer.current = hits(client) ? toNote(client) : null
    if (pan) {
      const el = scrollRef.current!
      el.scrollLeft = pan.left - (e.clientX - pan.x)
      el.scrollTop = pan.top - (e.clientY - pan.y)
    } else if (gesture && !gesture.done) {
      const frame = scrollRef.current!.closest('.expanded')!.getBoundingClientRect()
      setGesture({ ...gesture, current: toNote(client), client, outside: !contains(frame, client) })
    }
  }

  function onPointerUp() {
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
      const image = images.find((i) => i.id === id)!
      const at = boardPoint(gesture.client)
      if (!at) {
        setGesture(null)
        return
      }
      setGesture({ ...gesture, done: true })
      setSelection(null)
      const grab = { x: gesture.start.x - image.x, y: gesture.start.y - image.y }
      void settle(() => api.updateImage(id, { detach: true, ...snapPoint({ x: at.x - grab.x, y: at.y - grab.y }) }))
      return
    }
    setGesture({ ...gesture, done: true })
    if (gesture.kind === 'block' || gesture.kind === 'block-resize') {
      const { x, y, width, height } = blockRect(note.blocks.find((b) => b.id === id)!)
      void settle(() => api.updateNoteBlock(note.id, id, gesture.kind === 'block' ? { x, y } : { width, height }))
    } else {
      const { x, y, width } = imageRect(images.find((i) => i.id === id)!)
      void settle(() => api.updateImage(id, gesture.kind === 'image' ? { x, y } : { width }))
    }
  }

  const blocks = note.blocks.map((block) => ({ block, rect: blockRect(block) }))
  const placed = images.map((image) => ({ image, rect: imageRect(image) }))
  const ghost = incoming && { ...incoming.image, ...onNoteCanvas(incoming.at, incoming.image) }
  const items: Rect[] = [...blocks.map((b) => b.rect), ...placed.map((p) => p.rect), ...(ghost ? [ghost] : [])]
  const extent = {
    width: Math.max(view.width / zoom, CANVAS_MARGIN + Math.max(0, ...items.map((r) => r.x + r.width))),
    height: Math.max(view.height / zoom, CANVAS_MARGIN + Math.max(0, ...items.map((r) => r.y + r.height))),
  }
  const leavingImage = leaving && images.find((i) => i.id === leaving.id)
  const selectedImage = !readOnly && selection?.kind === 'image' ? images.find((i) => i.id === selection.id) : undefined
  const toRemove =
    pendingRemove?.kind === 'block'
      ? note.blocks.find((b) => b.id === pendingRemove.id)
      : pendingRemove && images.find((i) => i.id === pendingRemove.id)

  return (
    <div className={`note-viewport${incoming ? ' drop-over' : ''}`}>
      <div
        ref={scrollRef}
        className={`note-scroll${fileOver ? ' file-over' : ''}`}
        tabIndex={-1}
        aria-label="Post-it content"
        onKeyDown={(e) => {
          if (readOnly || !selection || (e.key !== 'Delete' && e.key !== 'Backspace')) return
          if (e.target instanceof Element && e.target.closest('input, textarea')) return
          e.preventDefault()
          e.stopPropagation()
          askRemove(selection.kind, selection.id)
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
            className={`note-canvas${pan ? ' panning' : ''}`}
            style={{ width: extent.width, height: extent.height, transform: `scale(${zoom})` }}
            onPointerDown={onCanvasPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={onPointerUp}
            onPointerCancel={() => {
              setGesture(null)
              setPan(null)
            }}
            // Losing the pointer without a release (another window took it) ends a pan or an unfinished drag.
            onLostPointerCapture={() => {
              setPan(null)
              setGesture((g) => (g && !g.done ? null : g))
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
                  lifted={dragging?.id === image.id}
                  onPointerDown={(e) => onItemPointerDown(e, 'image', image.id)}
                  onResizePointerDown={(e) => onItemPointerDown(e, 'image-resize', image.id)}
                  onDelete={readOnly ? undefined : () => askRemove('image', image.id)}
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
                readOnly={readOnly}
                onHeadPointerDown={(e) => onItemPointerDown(e, 'block', block.id)}
                onResizePointerDown={(e) => onItemPointerDown(e, 'block-resize', block.id)}
                onSelect={() => setSelection({ kind: 'block', id: block.id })}
                onTitle={(title) => void saveBlock(block, { title })}
                onRemove={() => askRemove('block', block.id)}
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
                    autoFocus={focusBlock === block.id}
                    readOnly={readOnly}
                    onChange={(items) => saveBlock(block, { items })}
                  />
                )}
                {block.kind === 'Links' && (
                  <LinksBlock
                    links={block.links ?? []}
                    note={note}
                    notes={notes}
                    autoFocus={focusBlock === block.id}
                    onChange={(links) => saveBlock(block, { links })}
                  />
                )}
              </BlockFrame>
            ))}

            {ghost && (
              <div className="incoming-image" aria-hidden="true" style={{ left: ghost.x, top: ghost.y, width: ghost.width, height: ghost.height }}>
                <img src={ghost.contentUrl} alt="" draggable={false} />
              </div>
            )}
          </div>
        </div>
      </div>

      {note.blocks.length === 0 && images.length === 0 && !incoming && (
        <p className="note-empty">{readOnly ? 'Nothing here yet.' : 'Add a note, a checklist or links from the bar below, or paste or drop an image.'}</p>
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

      {!readOnly && <NoteBar onAdd={addBlock} onAddImage={onPickImages} onDelete={onDeletePostIt} />}

      {selectedImage && (
        <ImageInspector
          key={selectedImage.id}
          image={selectedImage}
          onSave={(changes) => settle(() => api.updateImage(selectedImage.id, changes))}
          onDelete={() => setPendingRemove({ kind: 'image', id: selectedImage.id })}
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

      {pendingRemove && toRemove && (
        <ConfirmDialog
          title={pendingRemove.kind === 'block' ? 'Remove this block?' : 'Delete this image?'}
          message={
            'kind' in toRemove
              ? `"${toRemove.title ?? blockKinds[toRemove.kind].label}" and its content will be removed from this post-it. This can't be undone.`
              : `"${toRemove.caption ?? toRemove.fileName}" will be removed from this post-it. This can't be undone.`
          }
          confirmLabel={pendingRemove.kind === 'block' ? 'Remove' : 'Delete'}
          onConfirm={() => {
            remove(pendingRemove.kind, pendingRemove.id)
            scrollRef.current?.focus({ preventScroll: true })
          }}
          onCancel={() => {
            setPendingRemove(null)
            scrollRef.current?.focus({ preventScroll: true })
          }}
        />
      )}
    </div>
  )
}
