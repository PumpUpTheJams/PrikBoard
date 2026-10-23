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

/** One of someone's pins, pricked into the top-left corner of an item they have selected. */
export interface Pin {
  /** The connection and the item. */
  id: string
  key: string
  viewer: LiveViewer
  /** Where the needle goes in, in canvas pixels. */
  at: Point
}

/** How far into a selected item's top-left corner its pin goes, in canvas pixels (at most halfway into a small item). */
const PIN_INSET = { x: 20, y: 16 }

/** The heads of the others' pointers. Only for the people in `viewers`, so not your own other tabs. */
export function headsOf(cursors: LiveCursor[], viewers: LiveViewer[]): Head[] {
  const who = new Map(viewers.map((v) => [v.principalId, v]))
  return cursors.flatMap((c) => {
    const viewer = who.get(c.principalId)
    return viewer ? [{ key: c.key, viewer, at: { x: c.x, y: c.y }, updatedAt: c.updatedAt }] : []
  })
}

/**
 * The pins of the others' selections: one in each selected item that is on the board now (`rectOf`: where it is shown,
 * moving with drags; null for an item that is gone or folded away in a collapsed bucket).
 */
export function pinsOf(peers: Peers, viewers: LiveViewer[], rectOf: (id: string) => Rect | null): Pin[] {
  const who = new Map(viewers.map((v) => [v.principalId, v]))
  return peers.selections.flatMap((s) => {
    const viewer = who.get(s.principalId)
    if (!viewer) return []
    return s.items.flatMap((item) => {
      const rect = rectOf(item)
      if (!rect) return []
      const at = { x: rect.x + Math.min(PIN_INSET.x, rect.width / 2), y: rect.y + Math.min(PIN_INSET.y, rect.height / 2) }
      return [{ id: `${s.key}:${item}`, key: s.key, viewer, at }]
    })
  })
}

/** Where to go to find someone: the head of their newest pointer (of all their tabs), else their first pin. */
export function whereIs(heads: Head[], pins: Pin[], principalId: string): { key: string; at: Point } | undefined {
  const head = heads.filter((h) => h.viewer.principalId === principalId).toSorted((a, b) => b.updatedAt - a.updatedAt)[0]
  return head ?? pins.find((p) => p.viewer.principalId === principalId)
}
