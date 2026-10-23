import { type PointerEvent } from 'react'
import type { BucketDetail, ImageDetail, CardDetail, Point, Rect } from '../api/types'
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

interface ViewControlsProps {
  /** Undo and redo buttons; left out for viewers. */
  undo?: HistoryButton
  redo?: HistoryButton
  zoom: number
  onZoom: (direction: 1 | -1) => void
  onZoomReset: () => void
  /** The whole canvas, in canvas pixels: it can start left of and above 0,0. */
  extent: Rect
  /** The visible part of the canvas, in canvas pixels. */
  viewport: Rect
  buckets: BucketDetail[]
  cards: CardDetail[]
  /** Cards faded by the filters or the search. */
  dimmed?: (card: CardDetail) => boolean
  /** The search result being looked at. */
  current?: string | null
  /** Images on the canvas (not the ones attached to cards). */
  images: ImageDetail[]
  /** Scroll so that this canvas point is in the middle of the view. */
  onNavigate: (center: Point) => void
}

const mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
const shortcuts = { undo: mac ? '⌘Z' : 'Ctrl+Z', redo: mac ? '⇧⌘Z' : 'Ctrl+Shift+Z' }

/** Undo and redo, and the minimap above the zoom buttons, in the bottom-right corner of the board. */
export function ViewControls(props: ViewControlsProps) {
  const { undo, redo, zoom, onZoom, onZoomReset, extent, viewport, buckets, cards, dimmed, current, images, onNavigate } = props
  // The map shows the canvas and the view, which can reach into the empty margin around it.
  const left = Math.min(extent.x, viewport.x)
  const top = Math.min(extent.y, viewport.y)
  const width = Math.max(extent.x + extent.width, viewport.x + viewport.width) - left
  const height = Math.max(extent.y + extent.height, viewport.y + viewport.height) - top
  const scale = Math.min((MAP_WIDTH - 2 * MAP_PAD) / width, (MAP_HEIGHT - 2 * MAP_PAD) / height)
  const toMap = (r: Rect) => ({
    left: MAP_PAD + (r.x - left) * scale,
    top: MAP_PAD + (r.y - top) * scale,
    width: Math.max(2, r.width * scale),
    height: Math.max(2, r.height * scale),
  })

  function navigate(e: PointerEvent<HTMLDivElement>) {
    const rect = e.currentTarget.getBoundingClientRect()
    onNavigate({ x: left + (e.clientX - rect.left - MAP_PAD) / scale, y: top + (e.clientY - rect.top - MAP_PAD) / scale })
  }

  /** "Undo move card (Ctrl+Z)", or just "Undo" when there is nothing to undo. */
  const title = (kind: 'undo' | 'redo', button: HistoryButton) =>
    button.label
      ? `${kind === 'undo' ? 'Undo' : 'Redo'} ${button.label.toLowerCase()} (${shortcuts[kind]})`
      : kind === 'undo'
        ? 'Nothing to undo'
        : 'Nothing to redo'

  return (
    <div className="view-controls">
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
        <div
          className="minimap"
          aria-hidden="true"
          onPointerDown={(e) => {
            e.currentTarget.setPointerCapture(e.pointerId)
            navigate(e)
          }}
          onPointerMove={(e) => e.buttons === 1 && navigate(e)}
        >
          {buckets.map((b) => (
            <span key={b.id} className="minimap-bucket" style={toMap(b)} />
          ))}
          {images.map((i) => (
            <span key={i.id} className="minimap-image" style={toMap(i)} />
          ))}
          {cards.map((n) => (
            <span
              key={n.id}
              className={`minimap-card fill-${n.color.toLowerCase()}${dimmed?.(n) ? ' dimmed' : ''}${n.id === current ? ' current' : ''}`}
              style={toMap({ x: n.x, y: n.y, width: CARD_WIDTH, height: CARD_HEIGHT })}
            />
          ))}
          <span className="minimap-viewport" style={toMap(viewport)} />
        </div>
        <ZoomButtons zoom={zoom} onStep={onZoom} onReset={onZoomReset} />
      </div>
    </div>
  )
}
