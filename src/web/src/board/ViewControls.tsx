import { memo, useSyncExternalStore, type PointerEvent, type ReactNode } from 'react'
import type { BucketDetail, ImageDetail, CardDetail, Point, Rect } from '../api/types'
import type { ScrollView } from './canvasHooks'
import { RedoIcon, UndoIcon } from './icons'
import { CARD_HEIGHT, CARD_WIDTH } from './layout'
import { ZoomButtons } from './ZoomButtons'

const MAP_WIDTH = 150
const MAP_HEIGHT = 96
const MAP_PAD = 4

/** An undo or redo button: `label` is what it would do (none: nothing to do). */
interface HistoryButton {
  label?: string
  disabled?: boolean
  onClick: () => void
}

/** The board's undo and redo buttons, also shown in the open card. */
export interface HistoryButtons {
  undo: HistoryButton
  redo: HistoryButton
}

/**
 * What the map shows, drawn where `left` and `top` (canvas pixels) is the map's corner, at `scale` map pixels per canvas
 * pixel. It is called again as the view scrolls, so it should return a memoized component (BoardMapItems, CardMapItems).
 */
type MapItems = (left: number, top: number, scale: number) => ReactNode

interface ViewControlsProps {
  /** Undo and redo buttons; left out for viewers. */
  undo?: HistoryButton
  redo?: HistoryButton
  zoom: number
  onZoom: (direction: 1 | -1) => void
  onZoomReset: () => void
  /** The whole canvas, in canvas pixels: it can start left of and above 0,0. */
  extent: Rect
  /** The canvas's scroll viewport, which the map follows (screen pixels). */
  view: ScrollView
  /** Turns the scroll viewport into the visible part of the canvas, in canvas pixels. */
  viewport: (view: Rect) => Rect
  items: MapItems
  /** Scroll so that this canvas point is in the middle of the view. */
  onNavigate: (center: Point) => void
  /** Another place than the board's bottom-right corner (`card-view-controls` in the open card). */
  className?: string
}

const mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
const shortcuts = { undo: mac ? '⌘Z' : 'Ctrl+Z', redo: mac ? '⇧⌘Z' : 'Ctrl+Shift+Z' }

/** Where the map shows a rectangle on the canvas: `left` and `top` are the canvas point at its corner, `scale` map pixels per canvas pixel. */
const toMap = (r: Rect, left: number, top: number, scale: number) => ({
  left: MAP_PAD + (r.x - left) * scale,
  top: MAP_PAD + (r.y - top) * scale,
  width: Math.max(2, r.width * scale),
  height: Math.max(2, r.height * scale),
})

/** Undo and redo, and the minimap above the zoom buttons, in the bottom-right corner of the board or the open card. */
export function ViewControls(props: ViewControlsProps) {
  const { undo, redo, zoom, onZoom, onZoomReset, className, ...map } = props

  /** "Undo move card (Ctrl+Z)", or just "Undo" when there is nothing to undo. */
  const title = (kind: 'undo' | 'redo', button: HistoryButton) =>
    button.label
      ? `${kind === 'undo' ? 'Undo' : 'Redo'} ${button.label.toLowerCase()} (${shortcuts[kind]})`
      : kind === 'undo'
        ? 'Nothing to undo'
        : 'Nothing to redo'

  return (
    <div className={`view-controls${className ? ` ${className}` : ''}`}>
      {undo && redo && (
        <div className="zoom-controls history-controls">
          <button
            type="button"
            className="rail-button small"
            aria-label={undo.label ? `Undo ${undo.label.toLowerCase()}` : 'Undo'}
            title={title('undo', undo)}
            disabled={!undo.label || undo.disabled}
            onClick={undo.onClick}
          >
            <UndoIcon />
          </button>
          <button
            type="button"
            className="rail-button small"
            aria-label={redo.label ? `Redo ${redo.label.toLowerCase()}` : 'Redo'}
            title={title('redo', redo)}
            disabled={!redo.label || redo.disabled}
            onClick={redo.onClick}
          >
            <RedoIcon />
          </button>
        </div>
      )}
      <div className="view-stack">
        <Minimap {...map} />
        <ZoomButtons zoom={zoom} onStep={onZoom} onReset={onZoomReset} />
      </div>
    </div>
  )
}

type MinimapProps = Pick<ViewControlsProps, 'extent' | 'view' | 'viewport' | 'items' | 'onNavigate'>

/** The canvas in small, with the part on screen outlined; it follows the scrolling by itself. Pressing it goes there. */
function Minimap({ extent, view, viewport, items, onNavigate }: MinimapProps) {
  const shown = viewport(useSyncExternalStore(view.subscribe, view.get))
  // The map shows the canvas and the view, which can reach into the empty margin around it.
  const left = Math.min(extent.x, shown.x)
  const top = Math.min(extent.y, shown.y)
  const width = Math.max(extent.x + extent.width, shown.x + shown.width) - left
  const height = Math.max(extent.y + extent.height, shown.y + shown.height) - top
  const scale = Math.min((MAP_WIDTH - 2 * MAP_PAD) / width, (MAP_HEIGHT - 2 * MAP_PAD) / height)

  function navigate(e: PointerEvent<HTMLDivElement>) {
    const rect = e.currentTarget.getBoundingClientRect()
    onNavigate({ x: left + (e.clientX - rect.left - MAP_PAD) / scale, y: top + (e.clientY - rect.top - MAP_PAD) / scale })
  }

  return (
    <div
      className="minimap"
      aria-hidden="true"
      onPointerDown={(e) => {
        e.currentTarget.setPointerCapture(e.pointerId)
        navigate(e)
      }}
      onPointerMove={(e) => e.buttons === 1 && navigate(e)}
    >
      {items(left, top, scale)}
      <span className="minimap-viewport" style={toMap(shown, left, top, scale)} />
    </div>
  )
}

/** Where the map's items are drawn, from MapItems. */
interface MapPlace {
  left: number
  top: number
  scale: number
}

interface BoardMapItemsProps extends MapPlace {
  buckets: BucketDetail[]
  cards: CardDetail[]
  /** Images on the canvas (not the ones attached to cards). */
  images: ImageDetail[]
  /** The search result being looked at. */
  current?: string | null
  /** The ids of the cards faded by the filters or the search, separated by spaces. */
  dimmedKey: string
  /** Where a big paste on its way lands. */
  pasting?: Rect | null
}

/** The board's buckets, images and cards: they only render again when they or the map's scale change, not as the view scrolls. */
export const BoardMapItems = memo(function BoardMapItems({ left, top, scale, buckets, cards, images, current, dimmedKey, pasting }: BoardMapItemsProps) {
  const dimmed = new Set(dimmedKey.split(' '))
  return (
    <>
      {buckets.map((b) => (
        <span key={b.id} className="minimap-bucket" style={toMap(b, left, top, scale)} />
      ))}
      {images.map((i) => (
        <span key={i.id} className="minimap-image" style={toMap(i, left, top, scale)} />
      ))}
      {cards.map((n) => (
        <span
          key={n.id}
          className={`minimap-card fill-${n.color.toLowerCase()}${dimmed.has(n.id) ? ' dimmed' : ''}${n.id === current ? ' current' : ''}`}
          style={toMap({ x: n.x, y: n.y, width: CARD_WIDTH, height: CARD_HEIGHT }, left, top, scale)}
        />
      ))}
      {pasting && <span className="minimap-paste" style={toMap(pasting, left, top, scale)} />}
    </>
  )
})

interface CardMapItemsProps extends MapPlace {
  components: Rect[]
  images: Rect[]
  drawings: Rect[]
}

/** The open card's components, images and drawings, where they are (a drag in progress shows on the map too). */
export const CardMapItems = memo(function CardMapItems({ left, top, scale, components, images, drawings }: CardMapItemsProps) {
  return (
    <>
      {images.map((r, i) => (
        <span key={`i${i}`} className="minimap-image" style={toMap(r, left, top, scale)} />
      ))}
      {components.map((r, i) => (
        <span key={`c${i}`} className="minimap-component" style={toMap(r, left, top, scale)} />
      ))}
      {drawings.map((r, i) => (
        <span key={`d${i}`} className="minimap-drawing" style={toMap(r, left, top, scale)} />
      ))}
    </>
  )
})
