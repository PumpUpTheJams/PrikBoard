import type { Point } from '../api/types'

/** Pointer travel (px) before a press on an item counts as a drag rather than a click. */
export const DRAG_THRESHOLD = 4

export const delta = (g: { start: Point; current: Point }): Point => ({ x: g.current.x - g.start.x, y: g.current.y - g.start.y })

export function isDrag(g: { start: Point; current: Point }) {
  const d = delta(g)
  return Math.abs(d.x) >= DRAG_THRESHOLD || Math.abs(d.y) >= DRAG_THRESHOLD
}
