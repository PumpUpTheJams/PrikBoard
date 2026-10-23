import type { RefObject } from 'react'
import { api } from '../api/client'
import type { BoardDetail, BucketDetail, CardDetail, DrawingDetail, GrowSide, ImageDetail, Point, Rect } from '../api/types'
import type { Gesture, GroupGesture, ItemDrag } from './boardGesture'
import { bucketItemsOf, count, itemsOf, type BucketItems, type Selection } from './boardItems'
import { linkCards, NO_DROPS, released, type ArrangeDrop, type AttachDrop, type DropProbeState, type DropTargets, type LinkDrop } from './dropTargets'
import type { ExpandedCardHandle } from './ExpandedCard'
import { CARD_HEIGHT, CARD_WIDTH, GRID, onCardCanvas } from './layout'
import { itemKindOf } from './liveGeometry'
import { pop, reducedMotion } from './motion'
import { bucketMove, ops, runAll } from './ops'
import { bounds } from './selection'
import type { TrashWindowHandle } from './TrashWindow'
import { patched, snap, step, target, type RecordingRun } from './undo'
import type { Notify } from './useToast'

interface BoardDropsOptions {
  board: BoardDetail
  /** The open card. */
  expanded: { id: string } | null
  cardHandle: RefObject<ExpandedCardHandle | null>
  trashHandle: RefObject<TrashWindowHandle | null>
  canvasRef: RefObject<HTMLDivElement | null>
  /** What the drag would be dropped on, as the last pointer move found it. */
  drops: DropTargets
  /** A group's spots in a bucket, while the group is dragged. */
  arrangeDrop: ArrangeDrop | null
  setDrops: (update: DropTargets | ((drops: DropTargets) => DropTargets)) => void
  probe: DropProbeState
  setGesture: (gesture: Gesture | null) => void
  selected: Selection[]
  setSelected: (items: Selection[]) => void
  setSelection: (item: Selection | null) => void
  /** Hides what was dropped on the trash until the reload removes it. */
  setBinned: (ids: string[]) => void
  /** Runs a change through the board, recorded for undo. */
  runRec: RecordingRun
  /** The same, letting go of the finished gesture afterwards. */
  settle: RecordingRun
  notify: Notify
  toCanvas: (e: { clientX: number; clientY: number }) => Point
  /** A card put at a point, naming the front bucket when it holds it there (`cardPlace`). */
  cardPlace: (at: Point) => Point & { bucketId?: string }
  toFront: (id: string, server?: boolean) => void
  /** Where items are shown right now (liveGeometry). */
  bucketRect: (bucket: BucketDetail) => Rect
  cardPoint: (card: CardDetail) => Point
  imageRect: (image: ImageDetail) => Rect
  drawingRect: (drawing: DrawingDetail) => Rect
  deleteCard: (id: string) => Promise<boolean>
  deleteSelected: () => Promise<boolean>
  putItems: (bucket: BucketDetail, items: BucketItems, grow: GrowSide) => Promise<boolean>
  moveGroup: (members: Selection[], label?: string) => void
}

/** What a drag on the board does when it is released, on what dropTargetsFor found under it. */
export function useBoardDrops(options: BoardDropsOptions) {
  const { board, expanded, cardHandle, trashHandle, canvasRef, drops, arrangeDrop, setDrops, probe, setGesture } = options
  const { selected, setSelected, setSelection, setBinned, runRec, settle, notify, toCanvas, cardPlace, toFront } = options
  const { bucketRect, cardPoint, imageRect, drawingRect, deleteCard, deleteSelected, putItems, moveGroup } = options

  const cardElement = (id: string) => canvasRef.current?.querySelector(`[data-card="${id}"]`)

  /**
   * An image dropped on a card on the board is sucked into it: it shrinks into the card's middle and fades, then the
   * card gives a little gulp. Unless the animation is canceled, the image stays hidden until the reload takes it away.
   */
  function suckInto(imageId: string, card: CardDetail): Animation | undefined {
    const el = canvasRef.current?.querySelector(`[data-image="${imageId}"]`)
    const image = board.images.find((i) => i.id === imageId)
    if (!el || !image) return undefined
    const from = imageRect(image)
    const to = cardPoint(card)
    const dx = to.x + CARD_WIDTH / 2 - (from.x + from.width / 2)
    const dy = to.y + CARD_HEIGHT / 2 - (from.y + from.height / 2)
    const keyframes = reducedMotion()
      ? [{ opacity: 0.6 }, { opacity: 0 }]
      : [
          { transform: 'translate(0, 0) scale(1)', opacity: 0.6 },
          { transform: `translate(${dx * 0.7}px, ${dy * 0.7}px) scale(0.35)`, opacity: 0.5, offset: 0.6 },
          { transform: `translate(${dx}px, ${dy}px) scale(0.02)`, opacity: 0 },
        ]
    const animation = el.animate(keyframes, { duration: 420, easing: 'cubic-bezier(0.5, 0, 0.75, 0)', fill: 'forwards' })
    animation.onfinish = () => pop(cardElement(card.id), 1.06)
    return animation
  }

  /** Lets go of a drag dropped on the trash can or the open trash window: what was dropped is gone at once, until `remove` is done. */
  function binDrop(g: Gesture, intoDeleted: boolean, ids: string[], remove: () => Promise<unknown>) {
    if (intoDeleted) trashHandle.current?.pop()
    setGesture({ ...g, done: true })
    setBinned(ids)
    void remove().then(() => setBinned([]))
  }

  /** Gives a collapsed bucket a little bounce once what was dropped on it has gone in. */
  const popFold = (bucketId: string) => (ok: boolean) => ok && pop(canvasRef.current?.querySelector(`[data-fold="${bucketId}"]`), 1.08)

  /** Links the cards dropped on a card (or the open one's window) there; they go back to their places, and all pop once it is saved. */
  function linkDropped(link: LinkDrop, cards: CardDetail[]) {
    setGesture(null)
    let added = 0
    void runRec(
      () => linkCards(link, cards),
      (result) => {
        added = result.added
        return result.entry
      },
    ).then((ok) => {
      if (!ok) return
      if (added === 0) return notify('Already linked')
      for (const card of cards) pop(cardElement(card.id))
      if (link.window) cardHandle.current?.pop()
      else pop(cardElement(link.card.id))
      notify(added === 1 ? 'Link created' : `${added} links created`)
    })
  }

  /** Attaches several images and drawings dragged together to a card. */
  function attachGroup(g: GroupGesture, { card, window: onWindow }: AttachDrop, client: Point) {
    const groupImages = itemsOf(board, g.members, 'image')
    const groupDrawings = itemsOf(board, g.members, 'drawing')
    const rects = new Map<string, Rect>([
      ...groupImages.map((i) => [i.id, imageRect(i)] as const),
      ...groupDrawings.map((d) => [d.id, drawingRect(d)] as const),
    ])
    // On the open card they land where the drag shows them. On a card on the board the drawings keep their places
    // relative to each other, from the top-left of the card's canvas.
    const at = onWindow ? cardHandle.current!.toCard(client) : null
    const drawn = bounds(groupDrawings.map((d) => rects.get(d.id)!))
    const spot = (id: string) => {
      const r = rects.get(id)!
      const p = at ? { x: at.x + r.x - g.current.x, y: at.y + r.y - g.current.y } : { x: GRID + r.x - drawn!.x, y: GRID + r.y - drawn!.y }
      return onCardCanvas(p, r)
    }
    // On a card on the board the images are sucked in, and the server finds each a free spot.
    const animations = onWindow ? [] : groupImages.map((i) => suckInto(i.id, card))
    const n = groupImages.length + groupDrawings.length
    setGesture({ ...g, done: true })
    setSelected([])
    void runAll(settle, `Attach ${count(n, 'item', 'items')}`, undefined, async (changes) => {
      await Promise.all(animations.map((a) => a?.finished.catch(() => {})))
      // One after another: on a card on the board each image's free spot is next to the ones before.
      for (const image of groupImages) {
        const after = await api.updateImage(image.id, onWindow ? { cardId: card.id, ...spot(image.id) } : { cardId: card.id })
        changes.push(patched(target.image(image.id), snap.image(image), snap.image(after), ['cardId', 'x', 'y']))
      }
      for (const drawing of groupDrawings) {
        const after = await api.updateDrawing(drawing.id, { cardId: card.id, ...spot(drawing.id) })
        changes.push(patched(target.drawing(drawing.id), snap.drawing(drawing), snap.drawing(after), ['cardId', 'x', 'y']))
      }
    }).then((ok) => {
      if (!ok) return animations.forEach((a) => a?.cancel())
      if (onWindow) cardHandle.current?.pop()
      notify(`${count(n, 'item', 'items')} embedded`)
    })
  }

  /** Saves where a dragged item went: onto the open card or into a card on the board, or else its new place or size. */
  function placeItem(g: ItemDrag, client: Point) {
    const { id } = g
    const onCard = expanded && probe.imageOnCard(g, client)
    const drawingInto = expanded && probe.drawingOnCard(g, client)
    const intoCard = g.kind === 'image' && !onCard && !probe.overWindow(client) ? probe.cardAt(toCanvas({ clientX: client.x, clientY: client.y })) : undefined
    if (expanded && drawingInto) {
      // Over cards on the board a drawing just moves.
      setSelection(null)
      const { drawing, at } = drawingInto
      void settle(...ops.editDrawing(drawing, { cardId: expanded.id, ...onCardCanvas(at, drawing) }, 'Put drawing on card', ['cardId', 'x', 'y']))
    } else if (expanded && onCard) {
      setSelection(null)
      const changes = { cardId: expanded.id, ...onCardCanvas(onCard.at, onCard.image) }
      void settle(...ops.editImage(onCard.image, changes, 'Embed image', ['cardId', 'x', 'y'])).then((ok) => ok && notify('Image embedded'))
    } else if (intoCard) {
      // Sucked into the card and attached in a free spot on its canvas, without opening it. The request waits for the
      // animation, so the reload doesn't take the image away halfway.
      setSelection(null)
      const animation = suckInto(id, intoCard)
      const image = board.images.find((i) => i.id === id)!
      const [embed, describe] = ops.editImage(image, { cardId: intoCard.id }, 'Embed image', ['cardId', 'x', 'y'])
      void settle(async () => {
        await animation?.finished.catch(() => {})
        return embed()
      }, describe).then((ok) => (ok ? notify('Image embedded') : animation?.cancel()))
    } else if (g.kind === 'card') {
      const card = board.cards.find((n) => n.id === id)!
      void settle(
        () => api.moveCard(id, cardPlace(cardPoint(card))),
        (moved) => step('Move card', [patched(target.card(id), snap.card(card), snap.card(moved), ['x', 'y'])]),
      )
    } else if (g.kind === 'image' || g.kind === 'image-resize') {
      const image = board.images.find((i) => i.id === id)!
      const { x, y, width } = imageRect(image)
      const moving = g.kind === 'image'
      void settle(...ops.editImage(image, moving ? { x, y } : { x, y, width }, moving ? 'Move image' : 'Resize image', ['x', 'y', 'width']))
    } else if (g.kind === 'drawing') {
      const drawing = board.drawings.find((d) => d.id === id)!
      const { x, y } = drawingRect(drawing)
      void settle(...ops.editDrawing(drawing, { x, y }, 'Move drawing', ['x', 'y']))
    } else if (g.kind === 'bucket') {
      // Undoing the move takes exactly the items it carried back.
      const bucket = board.buckets.find((b) => b.id === id)!
      const { x, y } = bucketRect(bucket)
      void settle(
        () => api.updateBucket(id, { x, y }),
        (moved) => step('Move bucket', bucketMove(board, bucket, moved)),
      )
    } else {
      // With a new size, a new place is a resize from a top or left corner or edge: the server leaves its items where they are.
      const bucket = board.buckets.find((b) => b.id === id)!
      const { x, y, width, height } = bucketRect(bucket)
      const resized = (after: BucketDetail) => step('Resize bucket', [patched(target.bucket(id), snap.bucket(bucket), snap.bucket(after), ['x', 'y', 'width', 'height'])])
      void settle(() => api.updateBucket(id, { x, y, width, height }), resized)
    }
  }

  return {
    /** Drops several selected items dragged together; anywhere else than on a drop target they move. */
    dropGroup(g: GroupGesture, client: Point) {
      const intoDeleted = drops.deleted !== null
      const fold = drops.fold ? board.buckets.find((b) => b.id === drops.fold) : undefined
      const arrange = arrangeDrop
      setDrops(released)
      if (probe.cancels(g, client)) {
        setDrops(NO_DROPS)
        setGesture(null)
        return
      }
      if (probe.onTrash(client) || intoDeleted) {
        // The toast's Undo puts them all back.
        setDrops(NO_DROPS)
        binDrop(g, intoDeleted, selected.map((s) => s.id), deleteSelected)
        return
      }
      const cards = itemsOf(board, g.members, 'card').toSorted((a, b) => a.y - b.y || a.x - b.x)
      if (fold) {
        // Out of sight with the others, each in a free spot the server finds.
        setGesture({ ...g, done: true })
        setSelected([])
        void putItems(fold, bucketItemsOf(board, g.members), 'Down').then(popFold(fold.id))
        return
      }
      if (arrange) {
        // In the spots shown while dragging (the same order as there). When they don't fit, the bucket grows over its
        // neighbors too, so the server puts it on top.
        setGesture({ ...g, done: true })
        toFront(arrange.bucket.id, false)
        void putItems(arrange.bucket, bucketItemsOf(board, g.members), arrange.side)
        return
      }
      const link = probe.linkDropFor(g, client)
      if (link) return linkDropped(link, cards)
      const attach = probe.attachDropFor(g, client)
      if (attach) return attachGroup(g, attach, client)
      setGesture({ ...g, done: true })
      moveGroup(g.members, g.grid ? 'Line up cards' : undefined)
    },

    /** Drops one item dragged by itself; anywhere else than on a drop target it moves (or keeps its new size). */
    dropItem(g: ItemDrag, client: Point) {
      const intoDeleted = drops.deleted !== null
      const fold = drops.fold ? board.buckets.find((b) => b.id === drops.fold) : undefined
      setDrops(released)
      if (probe.cancels(g, client)) {
        setGesture(null)
        return
      }
      if (g.kind === 'card' && (probe.onTrash(client) || intoDeleted)) {
        // The toast and Ctrl/Cmd+Z undo it.
        binDrop(g, intoDeleted, [g.id], () => deleteCard(g.id))
        return
      }
      if (fold) {
        // Out of sight with the others, in a free spot the server finds.
        setGesture({ ...g, done: true })
        void putItems(fold, bucketItemsOf(board, [{ kind: itemKindOf[g.kind], id: g.id }]), 'Down').then(popFold(fold.id))
        return
      }
      const link = probe.linkDropFor(g, client)
      if (link) return linkDropped(link, [board.cards.find((n) => n.id === g.id)!])
      setGesture({ ...g, done: true })
      placeItem(g, client)
    },
  }
}
