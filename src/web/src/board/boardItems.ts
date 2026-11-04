import type { BoardDetail, BucketDetail, CardDetail, DrawingDetail, ImageDetail, LabelDetail, Point, Rect } from '../api/types'
import { CARD_HEIGHT, CARD_WIDTH, type Size } from './layout'
import type { BucketCount } from './BucketView'
import type { Picked } from './selection'

/** The kinds of items on the board that can be selected. */
export type SelectionKind = 'card' | 'bucket' | 'image' | 'drawing' | 'label'

/** A selected item on the board. */
export interface Selection extends Picked<SelectionKind> {
  /** Just created: its name or title is focused so it can be typed straight away. */
  fresh?: boolean
}

/** The board's item of each kind. */
export interface ItemOfKind {
  card: CardDetail
  bucket: BucketDetail
  image: ImageDetail
  drawing: DrawingDetail
  label: LabelDetail
}

/** The kinds of item placed freely on a canvas, the board's or a card's, which are moved, attached, deleted and restored alike. */
export const CANVAS_KINDS = ['image', 'drawing', 'label'] as const
export type CanvasKind = (typeof CANVAS_KINDS)[number]
export type CanvasItem = ItemOfKind[CanvasKind]
export const isCanvasKind = (kind: string): kind is CanvasKind => (CANVAS_KINDS as readonly string[]).includes(kind)
/** Where lists of them are named by kind (a board's, a bucket's, a paste's). */
export const PLURAL = { image: 'images', drawing: 'drawings', label: 'labels' } as const

/** The board's items, by kind. */
export const itemLists = (board: BoardDetail): { [K in SelectionKind]: ItemOfKind[K][] } => ({
  card: board.cards,
  bucket: board.buckets,
  image: board.images,
  drawing: board.drawings,
  label: board.labels,
})

/** The board's items of one kind among selected items, in the order they were selected. */
export function itemsOf<K extends SelectionKind>(board: BoardDetail, members: Picked<string>[], kind: K): ItemOfKind[K][] {
  const list: ItemOfKind[K][] = itemLists(board)[kind]
  return members.flatMap((m) => (m.kind === kind ? list.filter((x) => x.id === m.id) : []))
}

/** The images, drawings and labels among selected items, with their kinds, images first. */
export const canvasMembers = (board: BoardDetail, members: Picked<string>[]) =>
  CANVAS_KINDS.flatMap((kind) => itemsOf(board, members, kind).map((item) => ({ kind, item })))

/** Where an item of `kind` sits (its top-left corner), if it is on the board. */
export function placeOf(board: BoardDetail, kind: SelectionKind, id: string): Point | undefined {
  const items: (Point & { id: string })[] = itemLists(board)[kind]
  return items.find((x) => x.id === id)
}

/** What kind of item an id is on the board, if it is on it. */
export function kindOf(board: BoardDetail, id: string): SelectionKind | undefined {
  const lists = itemLists(board)
  return (['card', 'bucket', 'image', 'drawing', 'label'] as const).find((kind) => lists[kind].some((x) => x.id === id))
}

/** An item on the board with its kind. */
export type KindedItem = { [K in SelectionKind]: { kind: K; item: ItemOfKind[K] } }[SelectionKind]

/** Every item on the board by id, to look many up at once (kindOf and bucketOf search the lists). */
export function itemsById(board: BoardDetail): Map<string, KindedItem> {
  const byId = new Map<string, KindedItem>()
  for (const item of board.cards) byId.set(item.id, { kind: 'card', item })
  for (const item of board.buckets) byId.set(item.id, { kind: 'bucket', item })
  for (const item of board.images) byId.set(item.id, { kind: 'image', item })
  for (const item of board.drawings) byId.set(item.id, { kind: 'drawing', item })
  for (const item of board.labels) byId.set(item.id, { kind: 'label', item })
  return byId
}

/** The bucket a selected card, image or drawing is in (null: loose). */
export function bucketOf(board: BoardDetail, item: Picked<SelectionKind>): string | null {
  if (item.kind === 'bucket') return null
  const list: { id: string; bucketId: string | null }[] = itemLists(board)[item.kind]
  return list.find((i) => i.id === item.id)?.bucketId ?? null
}

/** A card's rectangle with its top-left at a point. */
export const cardBox = ({ x, y }: Point): Rect => ({ x, y, width: CARD_WIDTH, height: CARD_HEIGHT })

/** Cards, images, drawings and labels going into a bucket together. */
export interface BucketItems {
  cards: CardDetail[]
  images: ImageDetail[]
  drawings: DrawingDetail[]
  labels: LabelDetail[]
}

const readingOrder = (a: Point, b: Point) => a.y - b.y || a.x - b.x

/** The cards, images, drawings and labels on the board itself among `members`, each in reading order, as they go into a bucket. */
export function bucketItemsOf(board: BoardDetail, members: Picked<string>[]): BucketItems {
  return {
    cards: itemsOf(board, members, 'card').toSorted(readingOrder),
    images: itemsOf(board, members, 'image').filter((i) => i.cardId === null).toSorted(readingOrder),
    drawings: itemsOf(board, members, 'drawing').filter((d) => d.cardId === null).toSorted(readingOrder),
    labels: itemsOf(board, members, 'label').filter((l) => l.cardId === null).toSorted(readingOrder),
  }
}

/** The items in the order the server packs them into a bucket: cards first, then images, drawings and labels. */
export const packed = ({ cards, images, drawings, labels }: BucketItems) => [...cards, ...images, ...drawings, ...labels]

/** Their sizes, in that order. */
export const packedSizes = (items: BucketItems): Size[] => [
  ...items.cards.map(() => ({ width: CARD_WIDTH, height: CARD_HEIGHT })),
  ...CANVAS_KINDS.flatMap((kind) => items[PLURAL[kind]].map(({ width, height }) => ({ width, height }))),
]

/** "1 card", "3 cards". */
export const count = (n: number, one: string, many: string) => (n === 1 ? `1 ${one}` : `${n} ${many}`)

/** A bucket with nothing in it. */
export const NOTHING_IN: BucketCount = { cards: 0, images: 0, drawings: 0, labels: 0 }

/** How many cards, images, drawings and labels (on the board, not on a card) each bucket holds, by bucket id. */
export function bucketCounts(cards: CardDetail[], images: ImageDetail[], drawings: DrawingDetail[], labels: LabelDetail[]): Map<string, BucketCount> {
  const counts = new Map<string, BucketCount>()
  const of = (bucketId: string) => {
    let count = counts.get(bucketId)
    if (!count) counts.set(bucketId, (count = { ...NOTHING_IN }))
    return count
  }
  for (const card of cards) if (card.bucketId) of(card.bucketId).cards++
  for (const image of images) if (image.bucketId && !image.cardId) of(image.bucketId).images++
  for (const drawing of drawings) if (drawing.bucketId && !drawing.cardId) of(drawing.bucketId).drawings++
  for (const label of labels) if (label.bucketId && !label.cardId) of(label.bucketId).labels++
  return counts
}

/**
 * Cards in the order the board draws them: bucket by bucket as the `buckets` are drawn (back to front), loose ones above
 * them all, and in each the most recently updated on top.
 */
export function drawOrder(cards: CardDetail[], buckets: BucketDetail[]): CardDetail[] {
  const rank = new Map(buckets.map((b, i) => [b.id, i]))
  const layer = (card: CardDetail) => (card.bucketId === null ? buckets.length : (rank.get(card.bucketId) ?? buckets.length))
  return cards.toSorted((a, b) => layer(a) - layer(b) || a.updatedAt.localeCompare(b.updatedAt))
}

/** How many of these items are on each card. */
export function countByCard(items: { cardId: string | null }[]) {
  const counts = new Map<string, number>()
  for (const { cardId } of items) if (cardId) counts.set(cardId, (counts.get(cardId) ?? 0) + 1)
  return counts
}
