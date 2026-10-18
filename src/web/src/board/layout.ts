// Client copy of the server's geometry rules (src/PrikBoard.Api/Domain/Layout.cs), used for live feedback while dragging.
// The server snaps and assigns buckets again on every change, so it stays the source of truth.
import type { BucketDetail, Point, Rect } from '../api/types'

export const GRID = 20
export const NOTE_WIDTH = 200
export const NOTE_HEIGHT = 120
export const MIN_BUCKET_WIDTH = NOTE_WIDTH + 2 * GRID
export const MIN_BUCKET_HEIGHT = NOTE_HEIGHT + 2 * GRID

export const snap = (value: number) => Math.round(value / GRID) * GRID

/** Snaps a top-left corner to the grid, keeping it on the canvas. */
export const snapPoint = ({ x, y }: Point): Point => ({ x: Math.max(0, snap(x)), y: Math.max(0, snap(y)) })

/** The topmost bucket containing a note's centre. `buckets` is back to front, as the API returns them. */
export function bucketUnderNote(buckets: BucketDetail[], note: Point): BucketDetail | undefined {
  const cx = note.x + NOTE_WIDTH / 2
  const cy = note.y + NOTE_HEIGHT / 2
  return buckets.findLast((b) => cx >= b.x && cx < b.x + b.width && cy >= b.y && cy < b.y + b.height)
}

/** The rectangle spanned by two corners. */
export function spanRect(a: Point, b: Point): Rect {
  return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(a.x - b.x), height: Math.abs(a.y - b.y) }
}
