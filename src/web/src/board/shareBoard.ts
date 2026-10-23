import type { BoardDetail } from '../api/types'

/** Whether two values read from the API hold the same: equal primitives, or arrays and objects with the same contents. */
export function same(a: unknown, b: unknown): boolean {
  if (a === b) return true
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => same(x, b[i]))
  }
  const ka = Object.keys(a)
  const kb = Object.keys(b)
  const x = a as Record<string, unknown>
  const y = b as Record<string, unknown>
  return ka.length === kb.length && ka.every((k) => k in y && same(x[k], y[k]))
}

/** `next` with every item that is the same as before (found by `key` in `prev`) replaced by that earlier copy; `prev` itself when nothing changed. */
function shareList<T>(prev: T[], next: T[], key: (item: T) => string): T[] {
  const before = new Map(prev.map((item) => [key(item), item]))
  let changed = prev.length !== next.length
  const list = next.map((item, i) => {
    const old = before.get(key(item))
    if (old === undefined || !same(old, item)) {
      changed = true
      return item
    }
    if (prev[i] !== old) changed = true
    return old
  })
  return changed ? list : prev
}

/**
 * A board just read, sharing what hasn't changed with the one shown before: each card, image, drawing, bucket, tag and
 * person that is the same keeps its object, and so does each list in which nothing changed, and the board itself when
 * nothing did. The board's memoized items (boardItemViews) then only render again for what a reload changed.
 */
export function shareBoard(prev: BoardDetail | null, next: BoardDetail): BoardDetail {
  if (!prev || prev.id !== next.id) return next
  const byId = (item: { id: string }) => item.id
  const shared: BoardDetail = {
    ...next,
    buckets: shareList(prev.buckets, next.buckets, byId),
    cards: shareList(prev.cards, next.cards, byId),
    images: shareList(prev.images, next.images, byId),
    drawings: shareList(prev.drawings, next.drawings, byId),
    tags: shareList(prev.tags, next.tags, byId),
    people: shareList(prev.people, next.people, (p) => p.principalId),
  }
  // The lists that didn't change are the same objects, so this compares only the board's own fields.
  return same(prev, shared) ? prev : shared
}
