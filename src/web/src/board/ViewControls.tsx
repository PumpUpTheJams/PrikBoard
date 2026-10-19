import { type PointerEvent } from 'react'
import type { BucketDetail, ImageDetail, NoteDetail, Point, Rect } from '../api/types'
import { MinusIcon, PlusIcon, RedoIcon, UndoIcon } from './icons'
import { NOTE_HEIGHT, NOTE_WIDTH } from './layout'
import { ZOOM_LEVELS } from './zoom'

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
  /** Size of the whole canvas, in canvas pixels. */
  extent: { width: number; height: number }
  /** The visible part of the canvas, in canvas pixels. */
  viewport: Rect
  buckets: BucketDetail[]
  notes: NoteDetail[]
  /** Post-its faded by the tag filter or the search. */
  dimmed?: (note: NoteDetail) => boolean
  /** The search result being looked at. */
  current?: string | null
  /** Images on the canvas (not the ones attached to notes). */
  images: ImageDetail[]
  /** Scroll so that this canvas point is in the middle of the view. */
  onNavigate: (center: Point) => void
}

const mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
const shortcuts = { undo: mac ? '⌘Z' : 'Ctrl+Z', redo: mac ? '⇧⌘Z' : 'Ctrl+Shift+Z' }

/** Undo and redo, and the minimap above the zoom buttons, in the bottom-right corner of the board. */
export function ViewControls(props: ViewControlsProps) {
  const { undo, redo, zoom, onZoom, onZoomReset, extent, viewport, buckets, notes, dimmed, current, images, onNavigate } = props
  // The map shows the canvas and the view, which can reach into the empty margin around it.
  const left = Math.min(0, viewport.x)
  const top = Math.min(0, viewport.y)
  const width = Math.max(extent.width, viewport.x + viewport.width) - left
  const height = Math.max(extent.height, viewport.y + viewport.height) - top
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

  /** "Undo move post-it (Ctrl+Z)", or just "Undo" when there is nothing to undo. */
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
          {notes.map((n) => (
            <span
              key={n.id}
              className={`minimap-note fill-${n.color.toLowerCase()}${dimmed?.(n) ? ' dimmed' : ''}${n.id === current ? ' current' : ''}`}
              style={toMap({ x: n.x, y: n.y, width: NOTE_WIDTH, height: NOTE_HEIGHT })}
            />
          ))}
          <span className="minimap-viewport" style={toMap(viewport)} />
        </div>
        <div className="zoom-controls">
          <button type="button" className="rail-button small" aria-label="Zoom out" disabled={zoom <= ZOOM_LEVELS[0]} onClick={() => onZoom(-1)}>
            <MinusIcon />
          </button>
          <button type="button" className="zoom-level" title="Reset to 100%" onClick={onZoomReset}>
            {Math.round(zoom * 100)}%
          </button>
          <button
            type="button"
            className="rail-button small"
            aria-label="Zoom in"
            disabled={zoom >= ZOOM_LEVELS[ZOOM_LEVELS.length - 1]}
            onClick={() => onZoom(1)}
          >
            <PlusIcon />
          </button>
        </div>
      </div>
    </div>
  )
}
