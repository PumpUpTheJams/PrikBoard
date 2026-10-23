import type { LiveViewer, Point, Rect } from '../api/types'
import type { LiveCursor, LiveDrag } from '../live/useLive'

/** Someone's thumbtack: where their pointer is, or pinned into the item they drag. */
export interface Tack {
  /** Their connection (one per tab). */
  key: string
  viewer: LiveViewer
  /** The needle's tip, in canvas pixels. */
  at: Point
  /** Pushed into the item they drag, and moving with it. */
  pinned: boolean
  updatedAt: number
}

/**
 * The thumbtacks of the others on the board: their pointers, and for those dragging, pinned into the item they hold
 * where they took hold of it (`grab`; the middle of its top edge without one). Only for the people in `viewers`, so
 * not your own other tabs.
 */
export function tacksOf(cursors: LiveCursor[], drags: LiveDrag[], viewers: LiveViewer[], heldRect: (drag: LiveDrag) => Rect | null): Tack[] {
  const who = new Map(viewers.map((v) => [v.principalId, v]))
  const tacks = new Map<string, Tack>()
  for (const cursor of cursors) {
    const viewer = who.get(cursor.principalId)
    if (viewer) tacks.set(cursor.key, { key: cursor.key, viewer, at: { x: cursor.x, y: cursor.y }, pinned: false, updatedAt: cursor.updatedAt })
  }
  for (const drag of drags) {
    const viewer = who.get(drag.principalId)
    const rect = !drag.endedAt && viewer ? heldRect(drag) : null
    if (!viewer || !rect) continue
    const grab = drag.grab ?? { x: 0.5, y: 0 }
    const at = { x: rect.x + grab.x * rect.width, y: rect.y + grab.y * rect.height }
    tacks.set(drag.key, { key: drag.key, viewer, at, pinned: true, updatedAt: drag.updatedAt })
  }
  return [...tacks.values()]
}

/** The newest thumbtack of one person (they may have several tabs open), or undefined when they point nowhere. */
export function tackOf(tacks: Tack[], principalId: string): Tack | undefined {
  return tacks.filter((t) => t.viewer.principalId === principalId).toSorted((a, b) => b.updatedAt - a.updatedAt)[0]
}
