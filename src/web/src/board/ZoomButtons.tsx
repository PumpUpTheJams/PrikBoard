import { MinusIcon, PlusIcon } from './icons'
import { MAX_ZOOM, MIN_ZOOM } from './zoom'

interface ZoomButtonsProps {
  zoom: number
  /** Zooms one level out (-1) or in (1). */
  onStep: (direction: 1 | -1) => void
  /** Back to 100%. */
  onReset: () => void
  /** Extra class names, e.g. card-zoom for a window's own canvas. */
  className?: string
}

/** Zoom out, the zoom level (which resets to 100%) and zoom in. */
export function ZoomButtons({ zoom, onStep, onReset, className }: ZoomButtonsProps) {
  return (
    <div className={className ? `zoom-controls ${className}` : 'zoom-controls'}>
      <button type="button" className="rail-button small" aria-label="Zoom out" disabled={zoom <= MIN_ZOOM} onClick={() => onStep(-1)}>
        <MinusIcon />
      </button>
      <button type="button" className="zoom-level" title="Reset to 100%" onClick={onReset}>
        {Math.round(zoom * 100)}%
      </button>
      <button type="button" className="rail-button small" aria-label="Zoom in" disabled={zoom >= MAX_ZOOM} onClick={() => onStep(1)}>
        <PlusIcon />
      </button>
    </div>
  )
}
