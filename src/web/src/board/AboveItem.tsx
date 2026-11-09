import type { MouseEvent, ReactNode } from 'react'
import type { Point } from '../api/types'
import { keepPress } from './gesture'
import { clearOfFloating, floatingOver, PANEL_GAP, PANEL_MARGIN, usePlaceOnCanvas } from './placeOnCanvas'

interface AboveItemProps {
  /** The item's top-left corner on the canvas; without one, the corner of the element this is in. */
  at?: Point
  /** How tall the item is in canvas pixels, read when the panel is placed (it goes below an item whose top is out of view). */
  height: (el: HTMLDivElement) => number
  zoom: number
  /** Measures again only when this changes (besides scrolls and resizes); without it, after every render. */
  measureKey?: string
  className?: string
  role?: string
  label?: string
  /** For a press inside, which otherwise only stops at the panel: a toolbar keeps the text being edited selected. */
  onMouseDown?: (e: MouseEvent<HTMLDivElement>) => void
  children: ReactNode
}

/**
 * A panel just above an item on a canvas, at the same size on screen at any zoom, kept in view and clear of what floats over
 * the canvas: below a short item when there is no room above it, else pinned to the top of the view.
 * Presses inside stay there: the canvas behind it doesn't start a gesture.
 */
export function AboveItem({ at, height, zoom, measureKey, className, role, label, onMouseDown, children }: AboveItemProps) {
  /** How far it slides to stay in view and clear of what floats over the canvas, in screen pixels, and how far down when it goes below the item. */
  const [panel, place] = usePlaceOnCanvas({ x: 0, y: 0, below: 0 }, (el, scroller, place) => {
    const view = scroller.getBoundingClientRect()
    const box = el.getBoundingClientRect()
    const left = box.left - place.x
    const right = left + box.width
    const above = box.top - place.y - place.below
    // Below a short item whose top is out of view; over a tall one it stays at the top of the view instead, near what is seen of it.
    const tall = height(el) * zoom
    const down = tall + box.height + 2 * PANEL_GAP
    const short = tall < (view.bottom - view.top) / 3
    const below = above < view.top + PANEL_MARGIN && short && above + down + box.height <= view.bottom - PANEL_MARGIN ? down : 0
    const top = above + below
    const edge = view.left + scroller.clientWidth - PANEL_MARGIN
    const inView = left < view.left + PANEL_MARGIN ? view.left + PANEL_MARGIN - left : right > edge ? Math.max(view.left + PANEL_MARGIN - left, edge - right) : 0
    const clear = clearOfFloating({ left: left + inView, top, right: right + inView, bottom: top + box.height }, floatingOver(scroller), { left: view.left, top: view.top, right: edge + PANEL_MARGIN, bottom: view.bottom })
    // Whole pixels, or rounding in the scale could keep it from ever settling.
    return { x: Math.round(inView + clear.x), y: Math.round(clear.y), below }
  }, measureKey)
  return (
    <div
      ref={panel}
      className={`above-item${className ? ` ${className}` : ''}`}
      role={role}
      aria-label={label}
      style={{ left: at?.x ?? 0, top: at?.y ?? 0, transform: `scale(${1 / zoom}) translate(${place.x}px, calc(-100% - ${PANEL_GAP}px + ${place.below + place.y}px))` }}
      onPointerDown={keepPress}
      onDoubleClick={(e) => e.stopPropagation()}
      onMouseDown={onMouseDown}
    >
      {children}
    </div>
  )
}
