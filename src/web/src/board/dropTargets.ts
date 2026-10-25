import { useRef, type RefObject } from 'react'
import { api } from '../api/client'
import type { BoardDetail, BucketDetail, CardDetail, GrowSide, Point, Rect } from '../api/types'
import type { Gesture, GroupGesture } from './boardGesture'
import { bucketItemsOf, cardBox } from './boardItems'
import type { IncomingDrawing, IncomingImage } from './CardCanvas'
import type { ExpandedCardHandle } from './ExpandedCard'
import { isDrag } from './gesture'
import { CARD_HEIGHT, CARD_WIDTH, GRID, bucketAtPointer, collapsedRect, defaultComponentSize, onCardCanvas, packIn } from './layout'
import { cardLabel, linksTo } from './links'
import { contains } from './selection'
import type { TrashWindowHandle } from './TrashWindow'
import { created, patched, snap, step, target, type UndoEntry } from './undo'

/**
 * Where a dragged card would be linked on release: the open card's window (`window`; `at` is the point on its
 * canvas, when over the canvas) or another card on the board.
 */
export interface LinkDrop {
  card: CardDetail
  window: boolean
  at?: Point
  /** The pointer, in screen pixels. */
  client: Point
}

/** Where several dragged images and drawings would be attached on release: the open card's window (`window`) or a card on the board. */
export interface AttachDrop {
  card: CardDetail
  window: boolean
}

/**
 * Several selected items dragged over a bucket their cards aren't all in: on release the cards go into its free spots,
 * and the images and drawings join it where they are let go.
 */
export interface ArrangeDrop {
  bucket: BucketDetail
  /** The edge nearest the pointer: the bucket grows that way when they don't fit. */
  side: GrowSide
  /** Where each of the group's cards goes, by id. */
  spots: Record<string, Point>
  /** The bucket as it would be after growing. */
  grown: Rect
}

/** What a drag in progress on the board would be dropped on, were it released now. */
export interface DropTargets {
  /** Back over where it started: released there, nothing changes. */
  home: boolean
  /** Over the rail's trash can (also for a new card carried from the rail). */
  trash: boolean
  /** Over the open trash window, which deletes it too: the pointer, in screen pixels. */
  deleted: Point | null
  /** The collapsed bucket a card or group of cards would go into. */
  fold: string | null
  /** The card a card or group of cards would be linked to, instead of moved. */
  link: LinkDrop | null
  /** The card a group of images and drawings would be attached to, instead of moved. */
  attach: AttachDrop | null
  /** The bucket a group's cards would be packed into, and where. */
  arrange: ArrangeDrop | null
  /** A board image over the open card's canvas, to attach it there. */
  incoming: IncomingImage | null
  /** Likewise a board drawing, to put it on the card's canvas. */
  incomingDrawing: IncomingDrawing | null
}

export const NO_DROPS: DropTargets = {
  home: false,
  trash: false,
  deleted: null,
  fold: null,
  link: null,
  attach: null,
  arrange: null,
  incoming: null,
  incomingDrawing: null,
}

/**
 * The drop targets after a release: none, but a group's spots in a bucket stay until the gesture ends, so the cards
 * don't jump back while the request is in flight.
 */
export const released = (drops: DropTargets): DropTargets => ({ ...NO_DROPS, arrange: drops.arrange })

/** What the board can tell about a screen point (`client`) or canvas point under a drag. */
export interface DropProbe {
  /** Whether releasing calls the drag off: back over where it started (see Gesture `home`). */
  cancels: (g: Gesture, client: Point) => boolean
  onTrash: (client: Point) => boolean
  /** Over the open trash window. */
  inDeleted: (client: Point) => boolean
  /** Over the open card's window. */
  overWindow: (client: Point) => boolean
  /** The collapsed bucket under a canvas point. */
  foldAt: (p: Point) => BucketDetail | undefined
  linkDropFor: (g: Gesture, client: Point) => LinkDrop | null
  arrangeFor: (g: Gesture) => ArrangeDrop | null
  attachDropFor: (g: Gesture, client: Point) => AttachDrop | null
  imageOnCard: (g: Gesture, client: Point) => IncomingImage | null
  drawingOnCard: (g: Gesture, client: Point) => IncomingDrawing | null
}

/** What a drag would be dropped on at a screen point, the first that applies in this order. */
export function dropTargetsFor(g: Gesture, client: Point, probe: DropProbe): DropTargets {
  if (probe.cancels(g, client)) return { ...NO_DROPS, home: true }
  if (!isDrag(g)) return NO_DROPS
  const deletable = g.kind === 'card' || g.kind === 'group'
  if (deletable && probe.onTrash(client)) return { ...NO_DROPS, trash: true }
  if (deletable && probe.inDeleted(client)) return { ...NO_DROPS, deleted: client }
  // Anything but a bucket can go into a collapsed one.
  const foldable = g.kind === 'card' || g.kind === 'image' || g.kind === 'drawing' || (g.kind === 'group' && g.members.every((m) => m.kind !== 'bucket'))
  const fold = foldable && !probe.overWindow(client) ? probe.foldAt(g.current) : undefined
  if (fold) return { ...NO_DROPS, fold: fold.id }
  const link = probe.linkDropFor(g, client)
  if (g.kind !== 'group') return { ...NO_DROPS, link, incoming: probe.imageOnCard(g, client), incomingDrawing: probe.drawingOnCard(g, client) }
  // Cards over a bucket they aren't all in go into its free spots (over the open card, they stay on the board).
  const arrange = !link && !probe.overWindow(client) ? probe.arrangeFor(g) : null
  return { ...NO_DROPS, link, arrange, attach: probe.attachDropFor(g, client) }
}

/**
 * Adds links to the `linked` cards it doesn't link to yet on `card`: into the Links component at `at` on its canvas,
 * else its first one, else a new one. Returns the undo step (null when nothing changed) and how many were added.
 */
export async function linkCards({ card, at }: LinkDrop, linked: CardDetail[]): Promise<{ entry: UndoEntry | null; added: number }> {
  const links = linked
    .filter((l) => l.id !== card.id && !card.components.some((b) => b.links?.some((x) => linksTo(x.url, l))))
    .map((l) => ({ title: cardLabel(l), url: l.path }))
  if (links.length === 0) return { entry: null, added: 0 }
  const label = links.length === 1 ? 'Link card' : 'Link cards'
  const lists = card.components.filter((b) => b.kind === 'Links')
  const component = (at && lists.find((b) => contains(b, at))) ?? lists[0]
  if (component) {
    const updated = await api.updateCardComponent(card.id, component.id, { links: [...(component.links ?? []), ...links] })
    const entry = step(label, [patched(target.component(card.id, component.id), snap.component(component), snap.component(updated), ['links'])])
    return { entry, added: links.length }
  }
  const place = at ? onCardCanvas({ x: at.x - GRID, y: at.y - GRID }, defaultComponentSize('Links')) : {}
  const added = await api.addCardComponent(card.id, { kind: 'Links', links, ...place })
  return { entry: step(label, [created(target.component(card.id, added.id), snap.component(added))]), added: links.length }
}

interface DropProbeOptions {
  board: BoardDetail
  /** The open card. */
  expanded: { id: string } | null
  cardHandle: RefObject<ExpandedCardHandle | null>
  trash: { open: boolean; ref: RefObject<TrashWindowHandle | null> }
  /** The rail's card button, a trash can while something is dragged. */
  railTrash: RefObject<HTMLButtonElement | null>
  toCanvas: (e: { clientX: number; clientY: number }) => Point
  /** The cards on the board, in the order they are drawn (the last on top). */
  drawn: CardDetail[]
  /** Buckets back to front. */
  stacked: BucketDetail[]
  /** The bucket drawn in front, with what is in it. */
  front: string | null
  bucketRect: (bucket: BucketDetail) => Rect
}

/** What the board can tell about the places a drag could be dropped on (a DropProbe), and the card under a point. */
export function useDropProbe(options: DropProbeOptions) {
  const { board, expanded, cardHandle, trash, railTrash, toCanvas, drawn, stacked, front, bucketRect } = options
  /** The last place a group was arranged in a bucket: a drag asks on every move, and the answer seldom changes. */
  const arranged = useRef<ArrangeMemo | null>(null)

  /**
   * Whether the front bucket covers canvas point `p` for an item outside it (`bucketId`: the item's bucket, or a
   * collapsed bucket's own id): its fill hides what is under it, so a drop there doesn't reach it.
   */
  function coveredAt(p: Point, bucketId: string | null): boolean {
    const bucket = front !== null && bucketId !== front ? board.buckets.find((b) => b.id === front) : undefined
    if (!bucket) return false
    return contains(bucketRect(bucket), p)
  }

  /** The topmost card on the board under a canvas point, other than `except` (one id, or the ids of a group), and not covered. */
  function cardAt(p: Point, except?: string | Set<string>): CardDetail | undefined {
    const skip = (id: string) => (typeof except === 'string' ? id === except : !!except?.has(id))
    return drawn.findLast((n) => !skip(n.id) && !coveredAt(p, n.bucketId) && contains(cardBox(n), p))
  }

  const cardUnder = (client: Point, except?: Set<string>) => cardAt(toCanvas({ clientX: client.x, clientY: client.y }), except)

  /** Whether a screen point is over the rail's trash can (its edges count). */
  function onTrash(client: Point) {
    const r = railTrash.current?.getBoundingClientRect()
    return !!r && client.x >= r.left && client.x <= r.right && client.y >= r.top && client.y <= r.bottom
  }

  const inDeleted = (client: Point) => trash.open && !!trash.ref.current?.contains(client)
  const overWindow = (client: Point) => !!cardHandle.current?.contains(client)

  const probe: DropProbe = {
    // The trash, the open trash window and the open card lie above the board: over them, a drag isn't back home.
    cancels: (g, client) => !!g.home && !!g.away && contains(g.home, g.current) && !onTrash(client) && !inDeleted(client) && !overWindow(client),
    onTrash,
    inDeleted,
    overWindow,
    // Not a collapsed bucket behind the front bucket.
    foldAt: (p) => stacked.findLast((b) => b.collapsed && !coveredAt(p, b.id) && contains(collapsedRect(bucketRect(b)), p)),
    // A card, or several selected cards, link to the open card (anywhere over its window) or to the card under the
    // pointer that isn't one of them.
    linkDropFor(g, client) {
      const ids = draggedCards(g)
      if (!ids) return null
      const win = cardHandle.current
      if (expanded && win?.contains(client)) {
        const open = !ids.has(expanded.id) ? board.cards.find((n) => n.id === expanded.id) : undefined
        return open ? { card: open, window: true, at: win.hits(client) ? win.toCard(client) : undefined, client } : null
      }
      const under = cardUnder(client, ids)
      return under ? { card: under, window: false, client } : null
    },
    arrangeFor: (g) => arrangeFor(g, board, stacked, front, arranged),
    // Only a group of images and drawings: to the open card (over its canvas) or to the card under the pointer.
    attachDropFor(g, client) {
      if (g.kind !== 'group' || !g.members.every((m) => m.kind === 'image' || m.kind === 'drawing')) return null
      const win = cardHandle.current
      if (expanded && win?.contains(client)) {
        const open = board.cards.find((n) => n.id === expanded.id)
        return open && win.hits(client) ? { card: open, window: true } : null
      }
      const under = cardUnder(client)
      return under ? { card: under, window: false } : null
    },
    // Where a board image would land on the open card's canvas, keeping the point it was grabbed at under the pointer.
    imageOnCard(g, client) {
      const card = cardHandle.current
      if (g.kind !== 'image' || !card?.hits(client)) return null
      const image = board.images.find((i) => i.id === g.id)
      if (!image) return null
      const at = card.toCard(client)
      return { image, at: { x: at.x - (g.start.x - image.x), y: at.y - (g.start.y - image.y) } }
    },
    // Likewise a board drawing.
    drawingOnCard(g, client) {
      const card = cardHandle.current
      if (g.kind !== 'drawing' || !card?.hits(client)) return null
      const drawing = board.drawings.find((d) => d.id === g.id)
      if (!drawing) return null
      const at = card.toCard(client)
      return { drawing, at: { x: at.x - (g.start.x - drawing.x), y: at.y - (g.start.y - drawing.y) } }
    },
  }
  return { ...probe, cardAt }
}

export type DropProbeState = ReturnType<typeof useDropProbe>

/** The cards a drag carries: one card, or a group of cards only; null for other drags. */
function draggedCards(g: Gesture): Set<string> | null {
  if (g.kind === 'card') return new Set([g.id])
  if (g.kind === 'group' && g.members.every((m) => m.kind === 'card')) return new Set(g.members.map((m) => m.id))
  return null
}

/**
 * Where the cards of several selected items being dragged would go into the bucket under the pointer, when they aren't
 * all in it already: its free spots, growing towards the edge nearest the pointer. Null for a group with buckets or
 * without cards: images and drawings stay where they are let go.
 */
function arrangeFor(g: Gesture, board: BoardDetail, stacked: BucketDetail[], front: string | null, memo: RefObject<ArrangeMemo | null>): ArrangeDrop | null {
  if (g.kind !== 'group' || g.members.some((m) => m.kind === 'bucket') || !g.members.some((m) => m.kind === 'card')) return null
  const p = g.current
  const bucket = bucketAtPointer(stacked, p, front)
  if (!bucket) return null
  const edges: [GrowSide, number][] = [
    ['Left', p.x - bucket.x],
    ['Right', bucket.x + bucket.width - p.x],
    ['Up', p.y - bucket.y],
    ['Down', bucket.y + bucket.height - p.y],
  ]
  const side = edges.reduce((a, b) => (b[1] < a[1] ? b : a))[0]
  // The places don't depend on where in the bucket the pointer is, only on the side it is nearest to: while the board,
  // the items, the bucket and that side stay the same, the last answer stands.
  const last = memo.current
  if (last && last.board === board && last.members === g.members && last.bucket === bucket && last.side === side) return last.drop
  const drop = arrangeInto(board, g.members, bucket, side)
  memo.current = { board, members: g.members, bucket, side, drop }
  return drop
}

/** What `arrangeFor` worked out last, and from what. */
interface ArrangeMemo {
  board: BoardDetail
  members: GroupGesture['members']
  bucket: BucketDetail
  side: GrowSide
  drop: ArrangeDrop | null
}

/** Where the cards among the selected `members` go in `bucket`, growing towards `side`; null when they are all in it already. */
function arrangeInto(board: BoardDetail, members: GroupGesture['members'], bucket: BucketDetail, side: GrowSide): ArrangeDrop | null {
  const cards = bucketItemsOf(board, members).cards
  // Moved around inside the bucket they are all in already, they go where they are dropped.
  if (cards.length === 0 || cards.every((n) => n.bucketId === bucket.id)) return null
  // What is in the way: the bucket's own cards and loose ones, not its neighbors' (the cards join it by id, so it may
  // grow over those, only as far as needed), and not images or drawings, which cards may go over.
  const ids = new Set(cards.map((n) => n.id))
  const taken = board.cards.filter((n) => !ids.has(n.id) && (n.bucketId === null || n.bucketId === bucket.id)).map(cardBox)
  const { bucket: grown, spots } = packIn(bucket, cards.map(() => ({ width: CARD_WIDTH, height: CARD_HEIGHT })), taken, side)
  return { bucket, side, grown, spots: Object.fromEntries(cards.map((n, i) => [n.id, spots[i]])) }
}
