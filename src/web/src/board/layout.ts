// Client copy of the server's geometry rules (src/PrikBoard.Api/Domain/Layout.cs), used for live feedback while dragging.
// The server snaps and assigns buckets again on every change, so it stays the source of truth.
import type { BucketDetail, DrawingDetail, ImageDetail, CardComponentKind, GrowSide, Point, Rect, ResizeHandle } from '../api/types'

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
export const MAX_BUCKET_SIZE = 10_000

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
 * Buckets in stacking order, back to front (Layout.BackToFront on the server): by `stackedAt`, ties in the order the
 * API lists them. Buckets are drawn in this order, and an item dropped where buckets overlap joins the topmost.
 */
export const backToFront = (buckets: BucketDetail[]) => buckets.toSorted((a, b) => Date.parse(a.stackedAt) - Date.parse(b.stackedAt))

/** A bucket or a loose drawing, as they stack on the board. */
export type Layer = { kind: 'bucket'; id: string; bucket: BucketDetail } | { kind: 'drawing'; id: string; drawing: DrawingDetail }

/** Whether a drawing is loose on the board: in no bucket and on no card, so it stacks with the buckets. */
export const isLooseDrawing = (drawing: DrawingDetail) => drawing.cardId === null && drawing.bucketId === null

/**
 * Buckets and loose drawings in one stack, back to front by `stackedAt` (ties: buckets first, as `backToFront`). They
 * are drawn in this order, each bucket with what is in it (drawings in a bucket too).
 */
export const layersBackToFront = (buckets: BucketDetail[], drawings: DrawingDetail[]): Layer[] =>
  [
    ...buckets.map((bucket): Layer => ({ kind: 'bucket', id: bucket.id, bucket })),
    ...drawings.filter(isLooseDrawing).map((drawing): Layer => ({ kind: 'drawing', id: drawing.id, drawing })),
  ].toSorted((a, b) => Date.parse(stackedAtOf(a)) - Date.parse(stackedAtOf(b)))

const stackedAtOf = (layer: Layer) => (layer.kind === 'bucket' ? layer.bucket.stackedAt : layer.drawing.stackedAt)

/**
 * The bucket a card, image or drawing put at `item` without a pointer joins (a sort, a cut pasted here): the topmost
 * one containing its center (Layout.Place on the server). The item then stays in it while buckets move around it.
 * `buckets` is back to front (`backToFront`). A collapsed bucket takes no new items, as on the server; it only counts
 * for its own item (`own`). `front`, the bucket this screen shows in front, wins wherever it contains the center: a
 * card put there names it (`bucketId` with `x` and `y`), so the card joins it. Drags go by the pointer instead
 * (`bucketAtPointer`).
 */
export function bucketUnder(buckets: BucketDetail[], item: Rect, own?: string | null, front?: string | null): BucketDetail | undefined {
  const cx = item.x + item.width / 2
  const cy = item.y + item.height / 2
  const under = buckets.filter((b) => cx >= b.x && cx < b.x + b.width && cy >= b.y && cy < b.y + b.height && (!b.collapsed || b.id === own))
  return under.find((b) => b.id === front) ?? under.at(-1)
}

/** The bucket a card dropped at `card` joins (`bucketUnder`). */
export const bucketUnderCard = (buckets: BucketDetail[], card: Point, own?: string | null, front?: string | null) =>
  bucketUnder(buckets, { ...card, width: CARD_WIDTH, height: CARD_HEIGHT }, own, front)

/**
 * The bucket anything dragged on this screen joins when it is let go with the pointer at `p`, wherever the item itself
 * lies (it may hang over the edge, or lie over a bucket the pointer is outside of): `front` where it covers `p`, else
 * the topmost open bucket there. The drop names it (`DropBucket`), so the server doesn't go by the item's center.
 * `buckets` is back to front, where this screen shows them.
 */
export function bucketAtPointer(buckets: BucketDetail[], p: Point, front?: string | null): BucketDetail | undefined {
  const under = buckets.filter((b) => !b.collapsed && p.x >= b.x && p.x < b.x + b.width && p.y >= b.y && p.y < b.y + b.height)
  return under.find((b) => b.id === front) ?? under.at(-1)
}

const COLUMN_STEP = CARD_WIDTH + GRID
const ROW_STEP = CARD_HEIGHT + GRID

/** The size of an item packed into a bucket. */
export interface Size {
  width: number
  height: number
}

/**
 * Where items put into a bucket by id go (Layout.PackIn on the server), one for each of `sizes` in turn: the first place
 * on the 20 px grid inside it, row by row, a grid step clear of `taken` (the bucket's own items and loose ones) and of
 * the items placed before it. When one doesn't fit, the bucket grows a row or column of card slots at a time towards
 * `grow` (or across, for an item too wide or too tall that way; left and up move that edge out), over its neighbors too,
 * downwards once that side reaches the limit, and to the right or left once it is as tall as it can be. Returns the
 * grown bucket and the places.
 */
export function packIn(bucket: Rect, sizes: Size[], taken: Rect[], grow: GrowSide): { bucket: Rect; spots: Point[] } {
  let rect = { x: bucket.x, y: bucket.y, width: bucket.width, height: bucket.height }
  // The free room it has fills first; growing only makes room for the rest.
  const spots: (Point | null)[] = sizes.map(() => null)
  const firstFree = (size: Size, from: Point, inTheWay: Blockers): Point | null => {
    const right = rect.x + Math.max(rect.width, MIN_BUCKET_WIDTH) - GRID
    const bottom = rect.y + Math.max(rect.height, MIN_BUCKET_HEIGHT) - GRID
    for (let y = from.y; y + size.height <= bottom; y += GRID) {
      for (let x = y === from.y ? from.x : rect.x + GRID; x + size.width <= right; ) {
        const around = { x: x - GRID, y: y - GRID, width: size.width + 2 * GRID, height: size.height + 2 * GRID }
        const blockedTo = inTheWay.rightEdgeAt(around)
        if (blockedTo === null) return { x, y }
        // Every grid step before a grid step past that edge would be in its way too.
        x += stepsPast(blockedTo + GRID - x, GRID)
      }
    }
    return null
  }
  const pack = () => {
    // Every place with the gap around it lies inside the bucket, so only what reaches into it can be in the way. Items
    // placed before the bucket grew keep their places.
    const area = { x: rect.x, y: rect.y, width: Math.max(rect.width, MIN_BUCKET_WIDTH), height: Math.max(rect.height, MIN_BUCKET_HEIGHT) }
    const inTheWay = new Blockers([...taken.filter((t) => intersects(t, area)), ...spots.flatMap((s, i) => (s ? [{ ...s, ...sizes[i] }] : []))])
    let last: Rect | null = null
    sizes.forEach((size, i) => {
      if (spots[i]) return
      // None of the places before the last one this pass found suit an item of its size, so the search goes on from there.
      const from = last && last.width === size.width && last.height === size.height ? last : { x: rect.x + GRID, y: rect.y + GRID }
      const spot = firstFree(size, from, inTheWay)
      spots[i] = spot
      if (!spot) return
      last = { ...spot, ...size }
      inTheWay.add(last)
    })
  }
  const grown = (side: GrowSide): Rect | null => {
    const columns = Math.floor((Math.max(rect.width, MIN_BUCKET_WIDTH) - GRID) / COLUMN_STEP)
    const rows = Math.floor((Math.max(rect.height, MIN_BUCKET_HEIGHT) - GRID) / ROW_STEP)
    const width = GRID + (columns + 1) * COLUMN_STEP
    const height = GRID + (rows + 1) * ROW_STEP
    if (side === 'Right') return width <= MAX_BUCKET_SIZE && rect.x + width <= MAX_COORDINATE ? { ...rect, width: Math.max(rect.width, width) } : null
    if (side === 'Down') return height <= MAX_BUCKET_SIZE && rect.y + height <= MAX_COORDINATE ? { ...rect, height: Math.max(rect.height, height) } : null
    if (side === 'Left')
      return rect.width + COLUMN_STEP <= MAX_BUCKET_SIZE && rect.x - COLUMN_STEP >= MIN_COORDINATE
        ? { ...rect, x: rect.x - COLUMN_STEP, width: rect.width + COLUMN_STEP }
        : null
    return rect.height + ROW_STEP <= MAX_BUCKET_SIZE && rect.y - ROW_STEP >= MIN_COORDINATE
      ? { ...rect, y: rect.y - ROW_STEP, height: rect.height + ROW_STEP }
      : null
  }
  /** The bucket grown for an item that found no place, as Layout.GrowFor does. */
  const grownFor = (size: Size): Rect | null => {
    const tooWide = size.width > Math.max(rect.width, MIN_BUCKET_WIDTH) - 2 * GRID
    const tooTall = size.height > Math.max(rect.height, MIN_BUCKET_HEIGHT) - 2 * GRID
    const vertical = grow === 'Down' || grow === 'Up'
    const across = vertical ? (tooWide ? (grown('Right') ?? grown('Left')) : null) : tooTall ? (grown('Down') ?? grown('Up')) : null
    // Then downwards, and to the right or left once it is as tall as it can be (Layout.GrowAny).
    return across ?? grown(grow) ?? grown('Down') ?? grown('Right') ?? grown('Left')
  }
  pack()
  for (let next = spots.indexOf(null); next >= 0; next = spots.indexOf(null)) {
    const bigger = grownFor(sizes[next])
    if (!bigger) break
    rect = bigger
    pack()
  }
  return { bucket: rect, spots: spots.map((s) => s ?? { x: rect.x + GRID, y: rect.y + GRID }) }
}

/** The distance of whole `step`s that reaches `distance` (at least one step). */
const stepsPast = (distance: number, step: number) => Math.max(1, Math.ceil(distance / step)) * step

/** The coarse grid's squares (Layout.Blockers on the server). */
const SQUARE = 512

/**
 * Rectangles in the way of placing something, filed by the squares of a coarse grid they reach into, so a search only
 * checks the ones near where it looks instead of all of them (Layout.Blockers on the server).
 */
class Blockers {
  private readonly squares = new Map<string, Rect[]>()

  constructor(rects: Rect[]) {
    for (const rect of rects) this.add(rect)
  }

  add(rect: Rect) {
    for (const key of squaresOf(rect)) {
      const list = this.squares.get(key)
      if (list) list.push(rect)
      else this.squares.set(key, [rect])
    }
  }

  /** The right edge of the rightmost rectangle that intersects `area`; null when none does. */
  rightEdgeAt(area: Rect): number | null {
    let edge: number | null = null
    for (const key of squaresOf(area)) {
      for (const rect of this.squares.get(key) ?? []) {
        if (intersects(rect, area) && (edge === null || rect.x + rect.width > edge)) edge = rect.x + rect.width
      }
    }
    return edge
  }
}

/** The squares a rectangle reaches into (an empty one counts where it sits). */
function squaresOf(rect: Rect): string[] {
  const left = Math.floor(rect.x / SQUARE)
  const top = Math.floor(rect.y / SQUARE)
  const right = Math.floor((rect.x + Math.max(1, rect.width) - 1) / SQUARE)
  const bottom = Math.floor((rect.y + Math.max(1, rect.height) - 1) / SQUARE)
  const keys: string[] = []
  for (let x = left; x <= right; x++) for (let y = top; y <= bottom; y++) keys.push(`${x},${y}`)
  return keys
}

/** The square a collapsed bucket folds into, in the top-right corner of `rect` (where the bucket is now, also mid-drag). */
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
 * A rectangle resized by dragging a corner or an edge by `d`. The opposite corner or edge stays put, and an edge only
 * changes the size in its own direction. The top-left never goes left of or above `min` (0 on a card's canvas,
 * MIN_COORDINATE on the board). `size` snaps and limits the new size, as the server does for that kind of item.
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
 * `taken`; null when there is none (the caller then lets the server pick a spot).
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

/**
 * Items in reading order: top to bottom, and left to right for items that start on the same grid row. The mobile
 * layout lists buckets, a bucket's cards and images, and a card's components and images this way, so each list follows
 * the canvas.
 */
export function readingOrder<T extends Point>(items: T[]): T[] {
  return [...items].sort((a, b) => snap(a.y) - snap(b.y) || a.x - b.x)
}

/** Where a new item of `size` goes on a card's canvas below everything on it (the mobile layout adds to the end). */
export function belowAll(taken: Rect[], size: { width: number; height: number }): Point {
  if (taken.length === 0) return { x: GRID, y: GRID }
  const left = Math.min(...taken.map((t) => t.x))
  const bottom = Math.max(...taken.map((t) => t.y + t.height))
  return onCardCanvas({ x: left, y: bottom + GRID }, size)
}
