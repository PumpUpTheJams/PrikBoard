// Client copy of the server's geometry rules (src/PrikBoard.Api/Domain/Layout.cs), used for live feedback while dragging.
// The server snaps and assigns buckets again on every change, so it stays the source of truth.
import type { BucketDetail, ImageDetail, CardComponentKind, Point, Rect, ResizeHandle } from '../api/types'

export const GRID = 20
export const CARD_WIDTH = 200
export const CARD_HEIGHT = 120
export const MIN_BUCKET_WIDTH = CARD_WIDTH + 2 * GRID
export const MIN_BUCKET_HEIGHT = CARD_HEIGHT + 2 * GRID

/** A collapsed bucket is a square this size in its top-right corner. */
export const COLLAPSED_SIZE = CARD_HEIGHT

export const MIN_IMAGE_WIDTH = 80
export const MAX_IMAGE_WIDTH = 4000

/** A card's own canvas (its components and attached images) runs from 0 to this, in both directions. */
export const CARD_CANVAS_MAX = 10_000
export const MIN_COMPONENT_WIDTH = 160
export const MIN_COMPONENT_HEIGHT = 80
export const MAX_COMPONENT_SIZE = 2000

/** The board has no edge at the origin: positions run from MIN_COORDINATE to MAX_COORDINATE, both ways. */
export const MIN_COORDINATE = -100_000
export const MAX_COORDINATE = 100_000

export const snap = (value: number) => Math.round(value / GRID) * GRID

/** A board position kept within the coordinate range, as the server keeps it. */
export const onBoard = (value: number) => Math.min(MAX_COORDINATE, Math.max(MIN_COORDINATE, value))

/** An image's display size for a width, as the server computes it: snapped, within limits, keeping the aspect ratio. */
export function imageSize(image: { pixelWidth: number; pixelHeight: number }, width: number) {
  const w = Math.min(MAX_IMAGE_WIDTH, Math.max(MIN_IMAGE_WIDTH, snap(width)))
  return { width: w, height: Math.max(1, Math.round((w * image.pixelHeight) / image.pixelWidth)) }
}

/** Snaps a top-left corner on the board to the grid, keeping it within the coordinate range. */
export const snapPoint = ({ x, y }: Point): Point => ({ x: onBoard(snap(x)), y: onBoard(snap(y)) })

/**
 * The topmost bucket containing a card's centre: the bucket a card dropped there joins (Layout.Place on the server). It
 * stays in that bucket while buckets move around it. `buckets` is back to front, as the API returns them. A collapsed
 * bucket only keeps the cards it has (`own`): others don't join it, as on the server.
 */
export function bucketUnderCard(buckets: BucketDetail[], card: Point, own?: string | null): BucketDetail | undefined {
  const cx = card.x + CARD_WIDTH / 2
  const cy = card.y + CARD_HEIGHT / 2
  return buckets.findLast((b) => cx >= b.x && cx < b.x + b.width && cy >= b.y && cy < b.y + b.height && (!b.collapsed || b.id === own))
}

/** The square a collapsed bucket folds up into: in its top-right corner (of `rect`, where the bucket is now, mid-drag too). */
export function collapsedRect(rect: Rect): Rect {
  return { x: rect.x + rect.width - COLLAPSED_SIZE, y: rect.y, width: COLLAPSED_SIZE, height: COLLAPSED_SIZE }
}

/**
 * Where the cards in the trash window go: a grid filling `width` (the visible width of its canvas), row by row, a grid
 * step around each. At least one column, however narrow.
 */
export function fitGrid(count: number, width: number): Point[] {
  const columns = Math.max(1, Math.floor((width - GRID) / (CARD_WIDTH + GRID)))
  return Array.from({ length: count }, (_, i) => ({
    x: GRID + (i % columns) * (CARD_WIDTH + GRID),
    y: GRID + Math.floor(i / columns) * (CARD_HEIGHT + GRID),
  }))
}

/** The rectangle spanned by two corners. */
export function spanRect(a: Point, b: Point): Rect {
  return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(a.x - b.x), height: Math.abs(a.y - b.y) }
}

/** The size a new component gets. */
export function defaultComponentSize(kind: CardComponentKind) {
  return kind === 'Checklist' ? { width: 340, height: 260 } : kind === 'Links' ? { width: 360, height: 220 } : { width: 340, height: 220 }
}

/** A component's size after a resize, as the server computes it: snapped and within limits. */
export function componentSize(width: number, height: number) {
  return {
    width: Math.min(MAX_COMPONENT_SIZE, Math.max(MIN_COMPONENT_WIDTH, snap(width))),
    height: Math.min(MAX_COMPONENT_SIZE, Math.max(MIN_COMPONENT_HEIGHT, snap(height))),
  }
}

/** A bucket's size after a resize, as the server computes it: snapped, at least the minimum. */
export function bucketSize(width: number, height: number) {
  return { width: Math.max(MIN_BUCKET_WIDTH, snap(width)), height: Math.max(MIN_BUCKET_HEIGHT, snap(height)) }
}

/**
 * A rectangle resized by dragging a corner or an edge by `d`, the opposite corner or edge staying put (as long as the
 * top-left stays at or right of and below `min`: 0 on a card's canvas, MIN_COORDINATE on the board); an edge only
 * changes the size in its own direction. `size` snaps and limits the new size, as the server does for that kind of item.
 */
export function resizeFrom(
  rect: Rect,
  handle: ResizeHandle,
  d: Point,
  size: (width: number, height: number) => { width: number; height: number },
  min = 0,
): Rect {
  const left = handle.endsWith('Left')
  const right = handle.endsWith('Right')
  const top = handle.startsWith('Top')
  const bottom = handle.startsWith('Bottom')
  const next = size(
    left ? Math.min(rect.width - d.x, rect.x + rect.width - min) : right ? rect.width + d.x : rect.width,
    top ? Math.min(rect.height - d.y, rect.y + rect.height - min) : bottom ? rect.height + d.y : rect.height,
  )
  return {
    x: left ? Math.max(min, rect.x + rect.width - next.width) : rect.x,
    y: top ? Math.max(min, rect.y + rect.height - next.height) : rect.y,
    ...next,
  }
}

/**
 * An image resized from a corner or edge. It keeps its aspect ratio, so the top and bottom edges set the width through
 * the height (the server only takes a width). `min` as for resizeFrom.
 */
export function resizeImage(image: ImageDetail, handle: ResizeHandle, d: Point, min = 0): Rect {
  const byHeight = handle === 'Top' || handle === 'Bottom'
  return resizeFrom(image, handle, d, (width, height) => imageSize(image, byHeight ? (height * image.pixelWidth) / image.pixelHeight : width), min)
}

/** Snaps a top-left corner on a card's canvas, keeping a `size` rectangle on it. */
export function onCardCanvas({ x, y }: Point, size: { width: number; height: number }): Point {
  return {
    x: Math.min(Math.max(0, snap(x)), Math.max(0, CARD_CANVAS_MAX - size.width)),
    y: Math.min(Math.max(0, snap(y)), Math.max(0, CARD_CANVAS_MAX - size.height)),
  }
}

export const intersects = (a: Rect, b: Rect) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height

/**
 * The first grid slot inside `area`, row by row, where a rectangle of `size` keeps a grid step clear of everything in
 * `taken`; null when there is none (the server then finds a spot further out).
 */
export function freeSpot(taken: Rect[], size: { width: number; height: number }, area: Rect): Point | null {
  const left = Math.max(GRID, Math.ceil(area.x / GRID) * GRID)
  const top = Math.max(GRID, Math.ceil(area.y / GRID) * GRID)
  for (let y = top; y + size.height <= area.y + area.height; y += GRID) {
    for (let x = left; x + size.width <= area.x + area.width; x += GRID) {
      const around = { x: x - GRID, y: y - GRID, width: size.width + 2 * GRID, height: size.height + 2 * GRID }
      if (!taken.some((t) => intersects(t, around))) return { x, y }
    }
  }
  return null
}
