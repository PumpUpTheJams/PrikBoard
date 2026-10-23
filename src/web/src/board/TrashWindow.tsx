import { useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { api, errorMessage } from '../api/client'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { plural } from '../components/plural'
import type { ImageDetail, CardDetail, Point, TrashContents } from '../api/types'
import { isDrag } from './gesture'
import { CloseIcon, SearchIcon, TrashIcon } from './icons'
import { fitGrid, GRID, CARD_HEIGHT, CARD_WIDTH } from './layout'
import { Card } from './Card'
import { matchCard, searchTerms } from './search'
import type { TagColors } from './tagColors'
import { useFloatingWindow } from './useFloatingWindow'
import { WindowResizeHandles } from './WindowResizeHandles'
import { useZoom } from './useZoom'
import { stepZoom } from './zoom'
import { ZoomButtons } from './ZoomButtons'

/** What the board needs from the open trash: whether a card dragged on the board is over it, and a pop when one went in. */
export interface TrashWindowHandle {
  contains: (client: Point) => boolean
  pop: () => void
}

/**
 * A card dragged out of the window: the pointer (screen pixels), where on the card it was grabbed (card pixels),
 * and whether the pointer is outside the window.
 */
export interface OutgoingCard {
  card: CardDetail
  client: Point
  grab: Point
  outside: boolean
}

/** A press on a card in the window, in screen pixels; a drag once it travels far enough. */
interface Press {
  card: CardDetail
  start: Point
  current: Point
  grab: Point
  outside: boolean
}

const NO_INSET = { x: 0, y: 0 }

interface TrashWindowProps {
  boardId: string
  /** Changes with every reload of the board: the window then loads the trash again. */
  reload: unknown
  readOnly: boolean
  tagColors: TagColors
  handle: RefObject<TrashWindowHandle | null>
  /** A card from the board is being dragged over the window, to be deleted. */
  dropOver: boolean
  boardZoom: number
  onClose: () => void
  /** Shows a card as it is inside, read only. */
  onOpenCard: (card: CardDetail, images: ImageDetail[]) => void
  onPutBack: (card: CardDetail) => void
  /** Deletes a card in the trash for good; resolves false when that failed. */
  onPurge: (card: CardDetail) => Promise<boolean>
  /** Deletes everything in the trash for good (after the window asked); resolves false when that failed. */
  onEmpty: () => Promise<boolean>
  /** A card is being dragged out of the window (null once that ends). */
  onOutgoing: (drag: OutgoingCard | null) => void
  /** A card dragged out of the window was released outside it; false when it went nowhere (it stays in the trash). */
  onDropOut: (drag: OutgoingCard) => boolean
  /** Whether a drag is in progress, so the board holds back its refreshes. */
  onBusy: (busy: boolean) => void
}

/**
 * The trash opened up: a window floating over the board, like an expanded card, with its own small canvas. The cards
 * deleted in the last 7 days fill it in a grid, most recently deleted first, as wide as the window shows; the search at the
 * top filters them and the rest close up. A card goes back with its put-back button, or by dragging it out onto the
 * board.
 */
export function TrashWindow(props: TrashWindowProps) {
  const { boardId, reload, readOnly, tagColors, handle, dropOver, boardZoom, onClose, onOpenCard, onPutBack } = props
  const { onPurge, onEmpty, onOutgoing, onDropOut, onBusy } = props
  const dialogRef = useRef<HTMLElement>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  const { style, moving, startMove, startResize, contains, pop } = useFloatingWindow(dialogRef, 'prikboard.trashWindow.window')
  const { zoom, zoomTo } = useZoom(scrollRef, NO_INSET)
  const [contents, setContents] = useState<TrashContents | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string | null>(null)
  const [press, setPress] = useState<Press | null>(null)
  /** Asking whether to empty the trash. */
  const [confirmEmpty, setConfirmEmpty] = useState(false)
  /** Cards dropped out of the window: hidden until the next load no longer has them. */
  const [gone, setGone] = useState<Set<string>>(new Set())
  /** The scroll viewport in screen pixels, which the grid fits. */
  const [view, setView] = useState({ width: 0, height: 0 })
  /** The cards the last two presses were on: the canvas holds the pointer, so double-clicks reach it, not the card. */
  const presses = useRef<[string | null, string | null]>([null, null])

  useEffect(() => {
    let current = true
    api.listDeletedCards(boardId).then(
      (loaded) => {
        if (!current) return
        setContents(loaded)
        setGone(new Set())
        setError(null)
      },
      (failure) => current && setError(errorMessage(failure)),
    )
    return () => {
      current = false
    }
  }, [boardId, reload])

  useEffect(() => onBusy(press !== null), [press, onBusy])
  useEffect(() => () => onBusy(false), [onBusy])

  useEffect(() => searchRef.current?.focus({ preventScroll: true }), [])

  useLayoutEffect(() => {
    const el = scrollRef.current!
    const update = () => setView({ width: el.clientWidth, height: el.clientHeight })
    update()
    const observer = new ResizeObserver(update)
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  useImperativeHandle(handle, () => ({ contains, pop }))

  const terms = useMemo(() => searchTerms(query), [query])
  const all = (contents?.cards ?? []).filter((n) => !gone.has(n.id))
  const shown = all.filter((n) => terms.length === 0 || matchCard(n, terms) !== null)
  /** The grid fits what is visible of the canvas, so zooming out fits more columns. */
  const width = Math.max(CARD_WIDTH + 2 * GRID, Math.floor(view.width / zoom))
  const spots = fitGrid(shown.length, width)
  const height = Math.max(Math.floor(view.height / zoom), GRID + Math.max(0, ...spots.map((s) => s.y + CARD_HEIGHT)))
  const imagesOf = (cardId: string) => contents?.images.filter((i) => i.cardId === cardId) ?? []
  const dragging = press && isDrag(press) ? press : null

  function toCanvas(client: Point): Point {
    const r = canvasRef.current!.getBoundingClientRect()
    return { x: (client.x - r.left) / zoom, y: (client.y - r.top) / zoom }
  }

  function onCardPointerDown(e: PointerEvent<HTMLDivElement>, card: CardDetail, at: Point) {
    presses.current = [presses.current[1], card.id]
    setSelected(card.id)
    if (e.button !== 0) return
    e.stopPropagation()
    e.preventDefault()
    e.currentTarget.focus({ preventScroll: true })
    if (readOnly) return
    canvasRef.current!.setPointerCapture(e.pointerId)
    const client = { x: e.clientX, y: e.clientY }
    const p = toCanvas(client)
    setPress({ card, start: client, current: client, grab: { x: p.x - at.x, y: p.y - at.y }, outside: false })
  }

  function onPointerMove(e: PointerEvent) {
    if (!press) return
    const client = { x: e.clientX, y: e.clientY }
    const next = { ...press, current: client, outside: !contains(client) }
    setPress(next)
    if (isDrag(next)) onOutgoing({ card: next.card, client, grab: next.grab, outside: next.outside })
  }

  function onPointerUp() {
    if (!press) return
    setPress(null)
    onOutgoing(null)
    if (!dragging || !dragging.outside) return
    // Hidden until the reload takes it out of the trash, so it doesn't jump back into the grid meanwhile.
    if (onDropOut({ card: dragging.card, client: dragging.current, grab: dragging.grab, outside: true })) {
      setGone((g) => new Set(g).add(dragging.card.id))
    }
  }

  /** Hidden until the next load no longer has it. */
  async function purge(card: CardDetail) {
    if (await onPurge(card)) setGone((g) => new Set(g).add(card.id))
  }

  async function empty() {
    setConfirmEmpty(false)
    if (await onEmpty()) setGone(new Set(all.map((n) => n.id)))
  }

  function cancel() {
    if (!press) return
    setPress(null)
    onOutgoing(null)
  }

  const ghostZoom = dragging?.outside ? boardZoom : zoom

  return (
    <article
      ref={dialogRef}
      className={`expanded trash-window${moving ? ' moving' : ''}${dropOver ? ' drop-over' : ''}`}
      style={style}
      role="dialog"
      aria-modal="false"
      aria-label="Deleted items"
      tabIndex={-1}
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return
        e.stopPropagation()
        if (e.target === searchRef.current && query) setQuery('')
        else onClose()
      }}
    >
      <header className="band trash-band" onPointerDown={startMove}>
        <div className="band-row">
          <span className="trash-band-title">
            <TrashIcon size={18} />
            Deleted items
            <span className="count">{all.length}</span>
          </span>
          <div className="band-actions">
            {!readOnly && (
              <button
                type="button"
                className="band-button empty-trash"
                title="Delete everything in here for good"
                disabled={all.length === 0}
                onClick={() => setConfirmEmpty(true)}
              >
                <TrashIcon size={15} />
                Empty trash
              </button>
            )}
            <button type="button" className="band-button icon" aria-label="Close" title="Close (Esc)" onClick={onClose}>
              <CloseIcon />
            </button>
          </div>
        </div>
        <label className="trash-search">
          <SearchIcon />
          <input
            ref={searchRef}
            type="search"
            placeholder="Search deleted items"
            aria-label="Search deleted items"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {terms.length > 0 && <span className="trash-search-count">{plural(shown.length, 'match', 'matches')}</span>}
        </label>
      </header>

      <div className="card-viewport">
        <div ref={scrollRef} className="card-scroll trash-scroll" tabIndex={-1} aria-label="Deleted cards">
          <div className="canvas-sizer" style={{ width: width * zoom, height: height * zoom }}>
            <div
              ref={canvasRef}
              className="card-canvas trash-canvas"
              style={{ width, height, transform: `scale(${zoom})` }}
              onPointerDown={(e) => {
                if (e.target !== e.currentTarget) return
                presses.current = [presses.current[1], null]
                setSelected(null)
              }}
              onPointerMove={onPointerMove}
              onPointerUp={onPointerUp}
              onPointerCancel={cancel}
              onLostPointerCapture={cancel}
              onDoubleClick={() => {
                const [first, second] = presses.current
                const card = second && first === second && shown.find((n) => n.id === second)
                if (card) onOpenCard(card, imagesOf(card.id))
              }}
            >
              {shown.map((card, i) => (
                <div key={card.id} className={dragging?.card.id === card.id ? 'leaving' : undefined}>
                  <Card
                    card={card}
                    point={spots[i]}
                    images={imagesOf(card.id).length}
                    tagColors={tagColors}
                    selected={selected === card.id}
                    lifted={false}
                    highlight={terms.length > 0 ? terms : undefined}
                    onPointerDown={(e) => onCardPointerDown(e, card, spots[i])}
                    onExpand={() => onOpenCard(card, imagesOf(card.id))}
                    onPutBack={readOnly ? undefined : () => onPutBack(card)}
                    onPurge={readOnly ? undefined : () => void purge(card)}
                  />
                </div>
              ))}
            </div>
          </div>
        </div>

        {error ? (
          <p className="card-empty">{error}</p>
        ) : contents && shown.length === 0 ? (
          <p className="card-empty">
            {terms.length > 0 ? 'No deleted items match the search.' : 'Nothing was deleted in the last 7 days. Deleted cards stay here that long.'}
          </p>
        ) : null}

        <ZoomButtons className="card-zoom" zoom={zoom} onStep={(direction) => zoomTo(stepZoom(zoom, direction))} onReset={() => zoomTo(1)} />
      </div>

      <WindowResizeHandles onResize={startResize} />

      {/* A card on its way out follows the pointer: at the window's zoom inside it, at the board's outside. */}
      {dragging &&
        createPortal(
          <div
            className="drag-ghost card-ghost"
            aria-hidden="true"
            style={{
              left: dragging.current.x - dragging.grab.x * ghostZoom,
              top: dragging.current.y - dragging.grab.y * ghostZoom,
              transform: `scale(${ghostZoom})`,
            }}
          >
            <Card card={dragging.card} point={{ x: 0, y: 0 }} images={imagesOf(dragging.card.id).length} tagColors={tagColors} selected={false} lifted onPointerDown={() => {}} onExpand={() => {}} />
          </div>,
          document.body,
        )}

      {confirmEmpty &&
        createPortal(
          <ConfirmDialog
            title="Empty the trash?"
            message={`${all.length === 1 ? 'The card in here, with its' : `All ${all.length} cards in here, with their`} images and drawings, will be deleted for good. This can't be undone.`}
            confirmLabel="Empty trash"
            onConfirm={() => void empty()}
            onCancel={() => setConfirmEmpty(false)}
          />,
          document.body,
        )}
    </article>
  )
}
