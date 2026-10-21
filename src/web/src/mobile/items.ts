import type { BoardDetail, BucketDetail, CardDetail, ImageDetail } from '../api/types'
import { readingOrder } from '../board/layout'
import { LOOSE } from '../board/links'

/** What a bucket's list shows, in reading order: its cards and the images on the board inside it. */
export type BucketItem = { kind: 'card'; card: CardDetail; x: number; y: number } | { kind: 'image'; image: ImageDetail; x: number; y: number }

/** The cards and board images in a bucket (LOOSE: in none), top to bottom and left to right as on the board. */
export function bucketItems(board: BoardDetail, bucketId: string): BucketItem[] {
  const id = bucketId === LOOSE ? null : bucketId
  return readingOrder<BucketItem>([
    ...board.cards.filter((c) => c.bucketId === id).map((card) => ({ kind: 'card' as const, card, x: card.x, y: card.y })),
    ...board.images.filter((i) => i.cardId === null && i.bucketId === id).map((image) => ({ kind: 'image' as const, image, x: image.x, y: image.y })),
  ])
}

/** The buckets as the lists show them: in reading order, like the board's columns. */
export const bucketsInOrder = (board: BoardDetail): BucketDetail[] => readingOrder(board.buckets)

/** Whether the board has cards or images in no bucket, which then get a list of their own. */
export const hasLoose = (board: BoardDetail) => board.cards.some((c) => !c.bucketId) || board.images.some((i) => !i.cardId && !i.bucketId)

/** "6 cards · 1 image" */
export function itemCount(items: BucketItem[]) {
  const cards = items.filter((i) => i.kind === 'card').length
  const images = items.length - cards
  const parts = [`${cards} ${cards === 1 ? 'card' : 'cards'}`]
  if (images > 0) parts.push(`${images} ${images === 1 ? 'image' : 'images'}`)
  return parts.join(' · ')
}
