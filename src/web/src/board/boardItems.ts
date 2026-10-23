import type { BoardDetail, BucketDetail, CardDetail, DrawingDetail, ImageDetail, Point, Rect } from '../api/types'
import { CARD_HEIGHT, CARD_WIDTH, type Size } from './layout'
import type { Picked } from './selection'

/** The kinds of items on the board that can be selected. */
export type SelectionKind = 'card' | 'bucket' | 'image' | 'drawing'

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
}

/** The board's items, by kind. */
export const itemLists = (board: BoardDetail): { [K in SelectionKind]: ItemOfKind[K][] } => ({
  card: board.cards,
  bucket: board.buckets,
  image: board.images,
  drawing: board.drawings,
})

/** The board's items of one kind among selected items, in the order they were selected. */
export function itemsOf<K extends SelectionKind>(board: BoardDetail, members: Picked<string>[], kind: K): ItemOfKind[K][] {
  const list: ItemOfKind[K][] = itemLists(board)[kind]
  return members.flatMap((m) => (m.kind === kind ? list.filter((x) => x.id === m.id) : []))
}

/** Where an item of `kind` sits (its top-left corner), if it is on the board. */
export function placeOf(board: BoardDetail, kind: SelectionKind, id: string): Point | undefined {
  const items: (Point & { id: string })[] = itemLists(board)[kind]
  return items.find((x) => x.id === id)
}

/** What kind of item an id is on the board, if it is on it. */
export function kindOf(board: BoardDetail, id: string): SelectionKind | undefined {
  const lists = itemLists(board)
  return (['card', 'bucket', 'image', 'drawing'] as const).find((kind) => lists[kind].some((x) => x.id === id))
}

/** The bucket a selected card, image or drawing is in (null: loose). */
export function bucketOf(board: BoardDetail, item: Picked<SelectionKind>): string | null {
  if (item.kind === 'bucket') return null
  const list: { id: string; bucketId: string | null }[] = itemLists(board)[item.kind]
  return list.find((i) => i.id === item.id)?.bucketId ?? null
}

/** A card's rectangle with its top-left at a point. */
export const cardBox = ({ x, y }: Point): Rect => ({ x, y, width: CARD_WIDTH, height: CARD_HEIGHT })

/** Cards, images and drawings going into a bucket together. */
export interface BucketItems {
  cards: CardDetail[]
  images: ImageDetail[]
  drawings: DrawingDetail[]
}

const readingOrder = (a: Point, b: Point) => a.y - b.y || a.x - b.x

/** The cards, images and drawings on the board itself among `members`, each in reading order, as they go into a bucket. */
export function bucketItemsOf(board: BoardDetail, members: Picked<string>[]): BucketItems {
  return {
    cards: itemsOf(board, members, 'card').toSorted(readingOrder),
    images: itemsOf(board, members, 'image').filter((i) => i.cardId === null).toSorted(readingOrder),
    drawings: itemsOf(board, members, 'drawing').filter((d) => d.cardId === null).toSorted(readingOrder),
  }
}

/** The items in the order the server packs them into a bucket: cards first, then images, then drawings. */
export const packed = ({ cards, images, drawings }: BucketItems) => [...cards, ...images, ...drawings]

/** Their sizes, in that order. */
export const packedSizes = (items: BucketItems): Size[] => [
  ...items.cards.map(() => ({ width: CARD_WIDTH, height: CARD_HEIGHT })),
  ...items.images.map(({ width, height }) => ({ width, height })),
  ...items.drawings.map(({ width, height }) => ({ width, height })),
]

/** "1 card", "3 cards". */
export const count = (n: number, one: string, many: string) => (n === 1 ? `1 ${one}` : `${n} ${many}`)
