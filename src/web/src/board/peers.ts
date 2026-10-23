import type { LiveViewer, Point, Rect } from '../api/types'
import type { LiveCursor, Peers } from '../live/useLive'

/** Someone's pointer on the board: their head, centered on it. */
export interface Head {
  /** Their connection (one per tab). */
  key: string
  viewer: LiveViewer
  /** In canvas pixels. */
  at: Point
  updatedAt: number
}

/** One of someone's pins, pricked into the top of an item they have selected, in a row with the others' pins in it. */
export interface Pin {
  /** The connection and the item. */
  id: string
  key: string
  viewer: LiveViewer
  /** Where the needle goes in, in canvas pixels. */
  at: Point
}

/** Where a selected item's first pin goes in, in canvas pixels from its top-left corner (at most halfway into a small item). */
const PIN_INSET = { x: 20, y: 16 }
/** How far apart, on screen, the pins of several people in one item are: a little more than a pin's head (`.peer-pin`, 1.4×). */
const PIN_SPACING = 36
/** Where a card's title starts (its top padding, `.card` in index.css), and how far below its tip a pin's hole reaches, on screen. */
const CARD_TITLE_TOP = 12
const HOLE_BELOW = 4.5

/** Where on an item a pin can go: its rectangle as shown now, and whether it is a card (with a title to keep clear of). */
export interface PinPlace {
  rect: Rect
  card: boolean
}

/** The heads of the others' pointers. Only for the people in `viewers`, so not your own other tabs. */
export function headsOf(cursors: LiveCursor[], viewers: LiveViewer[]): Head[] {
  const who = new Map(viewers.map((v) => [v.principalId, v]))
  return cursors.flatMap((c) => {
    const viewer = who.get(c.principalId)
    return viewer ? [{ key: c.key, viewer, at: { x: c.x, y: c.y }, updatedAt: c.updatedAt }] : []
  })
}

/**
 * The pins of the others' selections: one in each selected item that is on the board now (`placeOf`: where it is shown,
 * moving with drags; null for an item that is gone or folded away in a collapsed bucket). Several people's pins in one
 * item stand in a row from its top-left corner, in the order they selected it, the same distance apart on screen at
 * any `zoom`. In a card, the hole stays above the title.
 */
export function pinsOf(peers: Peers, viewers: LiveViewer[], placeOf: (id: string) => PinPlace | null, zoom: number): Pin[] {
  const who = new Map(viewers.map((v) => [v.principalId, v]))
  const byItem = new Map<string, { key: string; viewer: LiveViewer; since: number }[]>()
  for (const s of peers.selections) {
    const viewer = who.get(s.principalId)
    if (!viewer) continue
    for (const item of s.items) byItem.set(item, [...(byItem.get(item) ?? []), { key: s.key, viewer, since: s.since[item] ?? 0 }])
  }
  return [...byItem].flatMap(([item, holders]) => {
    const place = placeOf(item)
    if (!place) return []
    const { rect, card } = place
    const y = card
      ? rect.y + Math.max(1 / zoom, CARD_TITLE_TOP - HOLE_BELOW / zoom)
      : rect.y + Math.min(PIN_INSET.y, rect.height / 2)
    const x = rect.x + Math.min(PIN_INSET.x, rect.width / 2)
    return holders
      .toSorted((a, b) => a.since - b.since || a.key.localeCompare(b.key))
      .map(({ key, viewer }, i) => ({ id: `${key}:${item}`, key, viewer, at: { x: x + (i * PIN_SPACING) / zoom, y } }))
  })
}

/** Where to go to find someone: the head of their newest pointer (of all their tabs), else their first pin. */
export function whereIs(heads: Head[], pins: Pin[], principalId: string): { key: string; at: Point } | undefined {
  const head = heads.filter((h) => h.viewer.principalId === principalId).toSorted((a, b) => b.updatedAt - a.updatedAt)[0]
  return head ?? pins.find((p) => p.viewer.principalId === principalId)
}
