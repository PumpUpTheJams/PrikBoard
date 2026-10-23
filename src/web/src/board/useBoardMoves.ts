import { api } from '../api/client'
import { cardColors, type BoardDetail, type BucketDetail, type CardDetail, type DrawingDetail, type GrowSide, type ImageDetail, type Point, type Rect } from '../api/types'
import { cardBox, count, itemsOf, type Selection } from './boardItems'
import { CARD_HEIGHT, CARD_WIDTH, GRID, snapPoint } from './layout'
import { allOf, bucketMove, runAll } from './ops'
import type { CardSort } from './SelectionToolbar'
import { bounds } from './selection'
import { deleted, patched, snap, target, type RecordingRun } from './undo'

interface BoardMovesOptions {
  board: BoardDetail
  selected: Selection[]
  setSelected: (items: Selection[]) => void
  /** The open card, which closes when it is deleted. */
  expanded: { id: string } | null
  closeCard: () => void
  /** Runs a change through the board, recorded for undo, then lets go of the finished gesture. */
  settle: RecordingRun
  /** A card put at a point, naming the front bucket when it holds it there (`cardPlace`). */
  cardPlace: (at: Point) => Point & { bucketId?: string }
  /** Where items are shown right now (liveGeometry). */
  bucketRect: (bucket: BucketDetail) => Rect
  cardPoint: (card: CardDetail) => Point
  imageRect: (image: ImageDetail) => Rect
  drawingRect: (drawing: DrawingDetail) => Rect
}

/** Changes to several items at once, each saved as one undo step that keeps what went through when a request fails. */
export function useBoardMoves(options: BoardMovesOptions) {
  const { board, selected, setSelected, expanded, closeCard, settle, cardPlace, bucketRect, cardPoint, imageRect, drawingRect } = options

  /** Deletes the selected items at once; buckets go but what is in them stays. */
  function deleteSelected() {
    const items = selected
    if (items.some((s) => s.kind === 'card' && s.id === expanded?.id)) closeCard()
    setSelected([])
    return runAll(settle, `Delete ${count(items.length, 'item', 'items')}`, `${count(items.length, 'item', 'items')} deleted`, (changes) =>
      allOf(
        items.map(async ({ kind, id }) => {
          if (kind === 'card') {
            const card = board.cards.find((n) => n.id === id)
            if (!card) return
            await api.deleteCard(id)
            changes.push(deleted(target.card(id), snap.card(card)))
          } else if (kind === 'bucket') {
            const bucket = board.buckets.find((b) => b.id === id)
            if (!bucket) return
            await api.deleteBucket(id)
            changes.push(deleted(target.bucket(id), snap.bucket(bucket)))
          } else if (kind === 'image') {
            const image = board.images.find((i) => i.id === id)
            if (!image) return
            await api.deleteImage(id)
            changes.push(deleted(target.image(id), snap.image(image)))
          } else {
            const drawing = board.drawings.find((d) => d.id === id)
            if (!drawing) return
            await api.deleteDrawing(id)
            changes.push(deleted(target.drawing(id), snap.drawing(drawing)))
          }
        }),
      ),
    )
  }

  /**
   * Saves a group move: each item goes where the drag shows it, shifted by `by` (for a cut pasted here). Buckets go
   * first, carrying what is in them, so the loose items that follow land among them as shown.
   */
  function moveGroup(members: Selection[], label = `Move ${count(members.length, 'item', 'items')}`, by: Point = { x: 0, y: 0 }) {
    const groupBuckets = itemsOf(board, members, 'bucket')
    const groupCards = itemsOf(board, members, 'card')
    const groupImages = itemsOf(board, members, 'image')
    const groupDrawings = itemsOf(board, members, 'drawing')
    // Work out every place now, as the drag shows it, before any request changes the board.
    const spots = new Map<string, Point>([
      ...groupBuckets.map((b) => [b.id, bucketRect(b)] as const),
      ...groupCards.map((n) => [n.id, cardPoint(n)] as const),
      ...groupImages.map((i) => [i.id, imageRect(i)] as const),
      ...groupDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    const to = (id: string) => ({ x: spots.get(id)!.x + by.x, y: spots.get(id)!.y + by.y })
    void runAll(settle, label, undefined, async (changes) => {
      await allOf(groupBuckets.map(async (b) => changes.push(...bucketMove(board, b, await api.updateBucket(b.id, to(b.id))))))
      await allOf([
        ...groupCards.map(async (n) => {
          const moved = await api.moveCard(n.id, cardPlace(to(n.id)))
          changes.push(patched(target.card(n.id), snap.card(n), snap.card(moved), ['x', 'y']))
        }),
        ...groupImages.map(async (i) => {
          const moved = await api.updateImage(i.id, to(i.id))
          changes.push(patched(target.image(i.id), snap.image(i), snap.image(moved), ['x', 'y']))
        }),
        ...groupDrawings.map(async (d) => {
          const moved = await api.updateDrawing(d.id, to(d.id))
          changes.push(patched(target.drawing(d.id), snap.drawing(d), snap.drawing(moved), ['x', 'y']))
        }),
      ])
    })
  }

  /**
   * Puts cards into a bucket's free spots in one request (the server finds them, growing the bucket towards `grow` when
   * they don't fit), after moving the images and drawings that came along to where they were dropped.
   */
  function putCards(bucket: BucketDetail, cards: CardDetail[], grow: GrowSide, along: Selection[]) {
    const alongImages = itemsOf(board, along, 'image')
    const alongDrawings = itemsOf(board, along, 'drawing')
    const spots = new Map<string, Point>([
      ...alongImages.map((i) => [i.id, imageRect(i)] as const),
      ...alongDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    const to = (id: string) => ({ x: spots.get(id)!.x, y: spots.get(id)!.y })
    return runAll(settle, `Put ${count(cards.length, 'card', 'cards')} in ${bucket.name}`, undefined, async (changes) => {
      await allOf([
        ...alongImages.map(async (i) => {
          const moved = await api.updateImage(i.id, to(i.id))
          changes.push(patched(target.image(i.id), snap.image(i), snap.image(moved), ['x', 'y']))
        }),
        ...alongDrawings.map(async (d) => {
          const moved = await api.updateDrawing(d.id, to(d.id))
          changes.push(patched(target.drawing(d.id), snap.drawing(d), snap.drawing(moved), ['x', 'y']))
        }),
      ])
      const placed = await api.placeCardsInBucket(bucket.id, { cardIds: cards.map((n) => n.id), grow })
      // Undo reverses the order: the cards go back first, then the bucket shrinks to its old size.
      changes.push(patched(target.bucket(bucket.id), snap.bucket(bucket), snap.bucket(placed.bucket), ['x', 'y', 'width', 'height']))
      for (const after of placed.cards) {
        const before = cards.find((n) => n.id === after.id)
        if (before) changes.push(patched(target.card(after.id), snap.card(before), snap.card(after), ['x', 'y']))
      }
    })
  }

  /** Lays several selected cards out as a grid where they are, in the order asked: from their top-left, as many columns as fit. */
  function sortCards(sort: CardSort) {
    const cards = itemsOf(board, selected, 'card')
    if (cards.length < 2) return
    const tagRank = (card: CardDetail) => {
      const names = board.tags.map((t) => t.name)
      const ranks = card.tags.map((t) => names.indexOf(t)).filter((i) => i >= 0)
      return ranks.length > 0 ? Math.min(...ranks) : Number.MAX_SAFE_INTEGER
    }
    const order: Record<CardSort, (a: CardDetail, b: CardDetail) => number> = {
      number: (a, b) => a.number - b.number,
      // Untitled cards last.
      title: (a, b) => Number(!a.title.trim()) - Number(!b.title.trim()) || a.title.localeCompare(b.title) || a.number - b.number,
      tag: (a, b) => tagRank(a) - tagRank(b) || a.number - b.number,
      // In the order the fill picker shows them.
      color: (a, b) => cardColors.indexOf(a.color) - cardColors.indexOf(b.color) || a.number - b.number,
      edited: (a, b) => b.updatedAt.localeCompare(a.updatedAt),
    }
    const sorted = cards.toSorted(order[sort])
    const area = bounds(cards.map(cardBox))!
    const columns = Math.max(1, Math.floor((area.width + GRID) / (CARD_WIDTH + GRID)))
    const spot = (i: number) => snapPoint({ x: area.x + (i % columns) * (CARD_WIDTH + GRID), y: area.y + Math.floor(i / columns) * (CARD_HEIGHT + GRID) })
    void runAll(settle, `Sort ${count(cards.length, 'card', 'cards')}`, undefined, (changes) =>
      allOf(
        sorted.map(async (card, i) => {
          const moved = await api.moveCard(card.id, cardPlace(spot(i)))
          changes.push(patched(target.card(card.id), snap.card(card), snap.card(moved), ['x', 'y']))
        }),
      ),
    )
  }

  return { deleteSelected, moveGroup, putCards, sortCards }
}
