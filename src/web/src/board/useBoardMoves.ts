import { api } from '../api/client'
import { cardColors, type BoardDetail, type BucketDetail, type CardDetail, type DrawingDetail, type DropBucket, type GrowSide, type ImageDetail, type Point, type Rect } from '../api/types'
import { cardBox, count, itemsOf, packed, type BucketItems, type Selection } from './boardItems'
import { CARD_HEIGHT, CARD_WIDTH, GRID, snapPoint } from './layout'
import { allOf, bucketMove, isBlankCard, runAll } from './ops'
import type { CardSort } from './SelectionToolbar'
import { bounds } from './selection'
import { deleted, patched, snap, target, type RecordingRun, type UndoChange } from './undo'

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
  /** The bucket a drop with the pointer at a canvas point goes into, or loose (`dropBucket`). */
  dropBucket: (pointer: Point, item?: Rect) => DropBucket
  /** Where items are shown right now (liveGeometry). */
  bucketRect: (bucket: BucketDetail) => Rect
  cardPoint: (card: CardDetail) => Point
  imageRect: (image: ImageDetail) => Rect
  drawingRect: (drawing: DrawingDetail) => Rect
}

/** Where a dragged group was let go (`moveGroup`). */
export interface GroupDrop {
  /** The pointer, in canvas pixels. */
  pointer: Point
  /** The bucket the group's cards go into the free spots of, growing towards `side` (an ArrangeDrop). */
  into?: { bucket: BucketDetail; side: GrowSide } | null
}

/** Changes to several items at once, each saved as one undo step that keeps what went through when a request fails. */
export function useBoardMoves(options: BoardMovesOptions) {
  const { board, selected, setSelected, expanded, closeCard, settle, cardPlace, dropBucket, bucketRect, cardPoint, imageRect, drawingRect } = options

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
            // One with nothing on it is gone for good: nothing to undo.
            if (!isBlankCard(board, card)) changes.push(deleted(target.card(id), snap.card(card)))
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
   * first, carrying what is in them, so the loose items that follow land among them as shown. A group without buckets
   * let go with the pointer at `drop.pointer` goes into the bucket shown there where it reaches into it, and loose
   * elsewhere; with `drop.into`, its cards are packed into that bucket's free spots instead (as the drag showed them).
   */
  function moveGroup(members: Selection[], label = `Move ${count(members.length, 'item', 'items')}`, by: Point = { x: 0, y: 0 }, drop?: GroupDrop) {
    const groupBuckets = itemsOf(board, members, 'bucket')
    const groupCards = itemsOf(board, members, 'card')
    const groupImages = itemsOf(board, members, 'image')
    const groupDrawings = itemsOf(board, members, 'drawing')
    // Work out every place now, as the drag shows it, before any request changes the board.
    const spots = new Map<string, Rect>([
      ...groupBuckets.map((b) => [b.id, bucketRect(b)] as const),
      ...groupCards.map((n) => [n.id, cardBox(cardPoint(n))] as const),
      ...groupImages.map((i) => [i.id, imageRect(i)] as const),
      ...groupDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    const to = (id: string) => ({ x: spots.get(id)!.x + by.x, y: spots.get(id)!.y + by.y })
    // Items carried along with buckets go by their centers, as the buckets they land among were before.
    const pointer = drop && groupBuckets.length === 0 ? drop.pointer : null
    const placed = (id: string) => (pointer ? { ...to(id), ...dropBucket(pointer, { ...spots.get(id)!, ...to(id) }) } : to(id))
    const into = pointer && groupCards.length > 0 ? drop?.into : null
    void runAll(settle, into ? `Put ${count(members.length, 'item', 'items')} in ${into.bucket.name}` : label, undefined, async (changes) => {
      await allOf(groupBuckets.map(async (b) => changes.push(...bucketMove(board, b, await api.updateBucket(b.id, to(b.id))))))
      await allOf([
        into
          ? pack(changes, into.bucket, groupCards, into.side)
          : allOf(
              groupCards.map(async (n) => {
                const moved = await api.moveCard(n.id, pointer ? placed(n.id) : cardPlace(to(n.id)))
                changes.push(patched(target.card(n.id), snap.card(n), snap.card(moved), ['x', 'y']))
              }),
            ),
        ...groupImages.map(async (i) => {
          const moved = await api.updateImage(i.id, placed(i.id))
          changes.push(patched(target.image(i.id), snap.image(i), snap.image(moved), ['x', 'y']))
        }),
        ...groupDrawings.map(async (d) => {
          const moved = await api.updateDrawing(d.id, placed(d.id))
          changes.push(patched(target.drawing(d.id), snap.drawing(d), snap.drawing(moved), ['x', 'y']))
        }),
      ])
    })
  }

  /**
   * Puts cards, images and drawings into a bucket's free room in one request (dropped on a collapsed bucket): the
   * server finds their spots, each at its own size, around the cards there only, growing the bucket towards `grow` when
   * they don't fit.
   */
  function putItems(bucket: BucketDetail, items: BucketItems, grow: GrowSide) {
    const all = packed(items)
    const onlyCards = all.length === items.cards.length
    const label = `Put ${onlyCards ? count(all.length, 'card', 'cards') : count(all.length, 'item', 'items')} in ${bucket.name}`
    return runAll(settle, label, undefined, (changes) => pack(changes, bucket, items.cards, grow, items.images, items.drawings))
  }

  /** Packs items into a bucket's free room around its cards (placeItemsInBucket), and records the step's changes. */
  async function pack(changes: (UndoChange | null)[], bucket: BucketDetail, cards: CardDetail[], grow: GrowSide, images: ImageDetail[] = [], drawings: DrawingDetail[] = []) {
    const placed = await api.placeItemsInBucket(bucket.id, {
      cardIds: cards.map((n) => n.id),
      imageIds: images.map((i) => i.id),
      drawingIds: drawings.map((d) => d.id),
      grow,
      onlyCardsTakeRoom: true,
    })
    // Undo reverses the order: the items go back first, then the bucket shrinks to its old size.
    changes.push(patched(target.bucket(bucket.id), snap.bucket(bucket), snap.bucket(placed.bucket), ['x', 'y', 'width', 'height']))
    for (const after of placed.cards) {
      const before = cards.find((n) => n.id === after.id)
      if (before) changes.push(patched(target.card(after.id), snap.card(before), snap.card(after), ['x', 'y']))
    }
    for (const after of placed.images) {
      const before = images.find((i) => i.id === after.id)
      if (before) changes.push(patched(target.image(after.id), snap.image(before), snap.image(after), ['x', 'y']))
    }
    for (const after of placed.drawings) {
      const before = drawings.find((d) => d.id === after.id)
      if (before) changes.push(patched(target.drawing(after.id), snap.drawing(before), snap.drawing(after), ['x', 'y']))
    }
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

  return { deleteSelected, moveGroup, putItems, sortCards }
}
