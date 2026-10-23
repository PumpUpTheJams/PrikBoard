import type { BoardDetail, BucketDetail, CardDetail, DragKind, DrawingDetail, ImageDetail, Point, Rect, ResizeHandle } from '../api/types'
import type { LiveDrag } from '../live/useLive'
import type { Gesture, ItemGesture } from './boardGesture'
import { cardBox, itemsOf, kindOf, type Selection, type SelectionKind } from './boardItems'
import type { ArrangeDrop } from './dropTargets'
import { delta, isDrag } from './gesture'
import { CARD_HEIGHT, CARD_WIDTH, GRID, MIN_COORDINATE, bucketSize, collapsedRect, onBoard, resizeFrom, resizeImage, snapPoint } from './layout'

/** A drag applied on top of the server's layout: your own gesture, or someone else's relayed by live updates. */
export interface ActiveDrag {
  kind: ItemGesture
  id: string
  d: Point
  handle: ResizeHandle
}

/** A gesture that drags items (not drawing a bucket or the selection rectangle). */
export type DragGesture = Exclude<Gesture, { kind: 'draw' | 'select' }>

const fromDragKind: Record<Exclude<DragKind, 'Group'>, ItemGesture> = {
  Card: 'card',
  Bucket: 'bucket',
  Resize: 'resize',
  Image: 'image',
  ImageResize: 'image-resize',
}
/** Drags others are told about (not drawings, yet). */
const toDragKind = Object.fromEntries(Object.entries(fromDragKind).map(([k, v]) => [v, k])) as Partial<Record<ItemGesture, DragKind>>

/** The kind of item a press drags. */
export const itemKindOf: Record<ItemGesture, SelectionKind> = {
  card: 'card',
  bucket: 'bucket',
  resize: 'bucket',
  image: 'image',
  'image-resize': 'image',
  drawing: 'drawing',
}

/** Your gesture when it drags items and has gone far enough to be a drag; else null. */
export function draggingOf(gesture: Gesture | null): DragGesture | null {
  if (!gesture || gesture.kind === 'draw' || gesture.kind === 'select') return null
  return isDrag(gesture) ? gesture : null
}

/**
 * Several items moving together each move by the same distance; cards lined up in a grid go to their place in it,
 * moving on with the pointer, and cards going into a bucket (`spots`) wait in theirs.
 */
function groupDrags(board: BoardDetail, members: Selection[], d: Point, grid?: Record<string, Point>, spots?: Record<string, Point>): ActiveDrag[] {
  return members.map(({ kind, id }) => {
    const spot = spots?.[id]
    const cell = spot ?? grid?.[id]
    const card = cell && board.cards.find((n) => n.id === id)
    const along = spot ? { x: 0, y: 0 } : d
    return { kind, id, d: card ? { x: cell.x - card.x + along.x, y: cell.y - card.y + along.y } : d, handle: 'BottomRight' }
  })
}

/** The drags applied on top of the server's layout: yours (with a group's spots in a bucket), then everyone else's. */
export function activeDrags(board: BoardDetail, dragging: DragGesture | null, spots: Record<string, Point> | undefined, remoteDrags: LiveDrag[]): ActiveDrag[] {
  const own = (): ActiveDrag[] => {
    if (!dragging) return []
    if (dragging.kind === 'group') return groupDrags(board, dragging.members, delta(dragging), dragging.grid, spots)
    return [{ kind: dragging.kind, id: dragging.id, d: delta(dragging), handle: dragging.handle ?? 'BottomRight' }]
  }
  const remote = remoteDrags.flatMap((r): ActiveDrag[] => {
    const d = { x: r.dx, y: r.dy }
    if (r.kind !== 'Group') return [{ kind: fromDragKind[r.kind], id: r.id, d, handle: r.handle ?? 'BottomRight' }]
    return groupDrags(board, (r.items ?? []).flatMap((id) => (kindOf(board, id) ? [{ kind: kindOf(board, id)!, id }] : [])), d)
  })
  return [...own(), ...remote]
}

/**
 * Where shaken cards go: a grid as square as it can be, one grid step apart, in reading order, placed so the card held
 * stays where it is. Undefined unless the group is two or more cards and nothing else.
 */
export function gridFor(board: BoardDetail, heldId: string, members: Selection[]): Record<string, Point> | undefined {
  if (members.length < 2 || members.some((m) => m.kind !== 'card')) return undefined
  const cards = itemsOf(board, members, 'card').toSorted((a, b) => a.y - b.y || a.x - b.x)
  const held = cards.find((n) => n.id === heldId)
  if (!held || cards.length < 2) return undefined
  const columns = Math.ceil(Math.sqrt(cards.length))
  const cell = (i: number) => ({ x: (i % columns) * (CARD_WIDTH + GRID), y: Math.floor(i / columns) * (CARD_HEIGHT + GRID) })
  const own = cell(cards.indexOf(held))
  return Object.fromEntries(cards.map((n, i) => [n.id, { x: held.x - own.x + cell(i).x, y: held.y - own.y + cell(i).y }]))
}

/** Whether an item is lifted by your own drag in progress: by itself, or with the others selected. */
export function isLifted(dragging: DragGesture | null, id: string): boolean {
  if (!dragging || dragging.done) return false
  return dragging.kind === 'group' ? dragging.members.some((m) => m.id === id) : dragging.id === id
}

/** Your drag in progress as others are told about it, as a string so an unchanged drag sends nothing; null for none. */
export function sentDrag(dragging: DragGesture | null): string | null {
  if (!dragging || dragging.done) return null
  const { x: dx, y: dy } = delta(dragging)
  if (dragging.kind === 'group') return JSON.stringify({ kind: 'Group', id: dragging.id, dx, dy, items: dragging.members.map((m) => m.id) })
  const kind = toDragKind[dragging.kind]
  return kind ? JSON.stringify({ kind, id: dragging.id, dx, dy, handle: dragging.handle }) : null
}

/**
 * Where everything on the board is shown right now: the server's layout, with the drags in progress applied on top
 * (yours wins over someone else's). `arrange`: the bucket your group of cards is going into, grown to fit them.
 */
export function liveGeometry(board: BoardDetail, drags: ActiveDrag[], arrange: ArrangeDrop | null) {
  const dragOf = (id: string | null) => (id === null ? undefined : drags.find((g) => g.id === id))

  function bucketRect(bucket: BucketDetail): Rect {
    if (arrange?.bucket.id === bucket.id) return arrange.grown
    const drag = dragOf(bucket.id)
    switch (drag?.kind) {
      case 'bucket':
        return { ...bucket, ...snapPoint({ x: bucket.x + drag.d.x, y: bucket.y + drag.d.y }) }
      case 'resize':
        return { ...bucket, ...resizeFrom(bucket, drag.handle, drag.d, bucketSize, MIN_COORDINATE) }
      default:
        return bucket
    }
  }

  /** How far the bucket an item is in has moved with a drag (it carries the item along, as the server does); null when it isn't moving. */
  function carriedBy(bucketId: string | null): Point | null {
    if (dragOf(bucketId)?.kind !== 'bucket') return null
    const bucket = board.buckets.find((b) => b.id === bucketId)
    if (!bucket) return null
    const moved = bucketRect(bucket)
    return { x: moved.x - bucket.x, y: moved.y - bucket.y }
  }
  const along = (item: Point, by: Point): Point => ({ x: onBoard(item.x + by.x), y: onBoard(item.y + by.y) })
  const moved = (item: Point, d: Point) => snapPoint({ x: item.x + d.x, y: item.y + d.y })

  function cardPoint(card: CardDetail): Point {
    const drag = dragOf(card.id)
    if (drag?.kind === 'card') return moved(card, drag.d)
    const by = carriedBy(card.bucketId)
    return by ? along(card, by) : card
  }

  function imageRect(image: ImageDetail): Rect {
    const drag = dragOf(image.id)
    if (drag?.kind === 'image') return { ...image, ...moved(image, drag.d) }
    if (drag?.kind === 'image-resize') {
      const resized = resizeImage(image, drag.handle, drag.d, MIN_COORDINATE)
      return { ...image, ...resized, ...snapPoint(resized) }
    }
    if (drag) return image
    const by = carriedBy(image.bucketId)
    return by ? { ...image, ...along(image, by) } : image
  }

  function drawingRect(drawing: DrawingDetail): Rect {
    const drag = dragOf(drawing.id)
    if (drag?.kind === 'drawing') return { ...drawing, ...moved(drawing, drag.d) }
    const by = carriedBy(drawing.bucketId)
    return by ? { ...drawing, ...along(drawing, by) } : drawing
  }

  /** Where an item on the board is shown right now (moving or not); null when it isn't on the board. */
  function liveRect(kind: SelectionKind, id: string): Rect | null {
    switch (kind) {
      case 'card': {
        const card = board.cards.find((n) => n.id === id)
        return card ? cardBox(cardPoint(card)) : null
      }
      case 'bucket': {
        const bucket = board.buckets.find((b) => b.id === id)
        if (!bucket) return null
        return bucket.collapsed ? collapsedRect(bucketRect(bucket)) : bucketRect(bucket)
      }
      case 'drawing': {
        const drawing = board.drawings.find((d) => d.id === id && d.cardId === null)
        return drawing ? drawingRect(drawing) : null
      }
      case 'image': {
        const image = board.images.find((i) => i.id === id && i.cardId === null)
        return image ? imageRect(image) : null
      }
    }
  }

  /** Where someone else's drag is right now, to outline it with their name: one item, or each of several moving together. */
  function remoteRects(drag: LiveDrag): Rect[] {
    const ids = drag.kind === 'Group' ? (drag.items ?? []) : [drag.id]
    return ids.flatMap((id) => {
      const kind = drag.kind === 'Group' ? kindOf(board, id) : itemKindOf[fromDragKind[drag.kind]]
      const rect = kind && liveRect(kind, id)
      return rect ? [rect] : []
    })
  }

  return { dragOf, bucketRect, cardPoint, imageRect, drawingRect, liveRect, remoteRects }
}
