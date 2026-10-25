import type { Point } from '../api/types'

/** Pointer travel (px) before a press on an item counts as a drag rather than a click. */
export const DRAG_THRESHOLD = 4

export const delta = (g: { start: Point; current: Point }): Point => ({ x: g.current.x - g.start.x, y: g.current.y - g.start.y })

/**
 * Whether a press has become a drag: the pointer is past the threshold, or was once (`dragged`), so a drag brought back
 * close to where it began (shaken into a grid there, say) doesn't turn back into a click.
 */
export function isDrag(g: { start: Point; current: Point; dragged?: boolean }) {
  if (g.dragged) return true
  const d = delta(g)
  return Math.abs(d.x) >= DRAG_THRESHOLD || Math.abs(d.y) >= DRAG_THRESHOLD
}

/**
 * Keeps a press on a control inside an item (a button, a field) from reaching the canvas, so it doesn't start a drag and
 * the click stays the control's. The middle button still goes through, so it pans there like anywhere else.
 */
export function keepPress(e: { button: number; stopPropagation: () => void }) {
  if (e.button !== 1) e.stopPropagation()
}
