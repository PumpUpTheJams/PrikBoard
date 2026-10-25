import { api } from '../api/client'
import type {
  BoardDetail,
  BoardTag,
  BucketDetail,
  DrawingChanges,
  DrawingDetail,
  DrawingStroke,
  DropBucket,
  ImageChanges,
  NewDrawingStroke,
  ImageDetail,
  CardComponent,
  CardComponentChanges,
  CardChanges,
  CardDetail,
  TagChanges,
} from '../api/types'
import { loadDrawingStrokes, loadedStrokes } from './drawings'
import { onBoard } from './layout'

/**
 * Undo and redo for changes made in this tab. Each change is recorded as what it touched before and after (the fields
 * it changed, or the whole item when it was created or deleted). Undoing first checks a fresh board: if any of those
 * fields changed since (someone else, an agent, another tab) or someone is dragging the item, the step is refused with
 * the reason. Otherwise the old values are written back; deleted items come back through the restore endpoints, with
 * the same ids.
 */

/** Some fields of an item, as the API returns them. */
export type Snapshot = Record<string, unknown>

export type UndoTarget =
  | { kind: 'card'; id: string }
  | { kind: 'component'; cardId: string; id: string }
  /** `carry`: for a move, the cards, images and drawings it took along. */
  | { kind: 'bucket'; id: string; carry?: BucketCarry }
  | { kind: 'image'; id: string }
  | { kind: 'drawing'; id: string }
  /** A drawing's strokes and corner, as scaling or turning it changes them. */
  | { kind: 'shape'; id: string }
  /**
   * A stroke added to drawing `id` as its `index`th. `stroke` has its x and y measured from the drawing's corner before
   * it was added, so a redo puts it back on the drawing wherever that is now.
   */
  | { kind: 'stroke'; id: string; index: number; stroke: NewDrawingStroke }
  | { kind: 'tag'; id: string }

/** What a moved bucket took along: cards, and images and drawings on the board. */
export interface BucketCarry {
  cards: string[]
  images: string[]
  drawings: string[]
}

/** One item's part of a step: null `before` means it was created, null `after` that it was deleted. */
export interface UndoChange {
  target: UndoTarget
  before: Snapshot | null
  after: Snapshot | null
  /** Changed as a side effect (cards carried by a bucket): checked, but written by the bucket's move. */
  implied?: boolean
  /** The board the item is on, when that isn't the step's own (the originals of a cut, on the board they were cut from). */
  boardId?: string
}

export interface UndoEntry {
  id: number
  /** What the step did, e.g. "Move card". */
  label: string
  changes: UndoChange[]
  /** Consecutive steps with the same key become one (picking several tags in a row). */
  coalesce?: string
  /** Shown as a toast with an Undo button when recorded (for deletes, which don't ask first). */
  toast?: string
  at: number
}

export type UndoDirection = 'undo' | 'redo'

/** Runs a change through the board; `describe` turns its result into a step for the history. */
export type RecordingRun = <T>(action: () => Promise<T>, describe?: (result: T) => UndoEntry | null | undefined) => Promise<boolean>

let nextId = 1

export function undoEntry(label: string, changes: UndoChange[], options: { coalesce?: string; toast?: string } = {}): UndoEntry {
  return { id: nextId++, label, changes, ...options, at: Date.now() }
}

export const target = {
  card: (id: string): UndoTarget => ({ kind: 'card', id }),
  component: (cardId: string, id: string): UndoTarget => ({ kind: 'component', cardId, id }),
  bucket: (id: string, carry?: BucketCarry): UndoTarget => ({ kind: 'bucket', id, carry }),
  image: (id: string): UndoTarget => ({ kind: 'image', id }),
  drawing: (id: string): UndoTarget => ({ kind: 'drawing', id }),
  shape: (id: string): UndoTarget => ({ kind: 'shape', id }),
  stroke: (drawingId: string, index: number, stroke: NewDrawingStroke): UndoTarget => ({ kind: 'stroke', id: drawingId, index, stroke }),
  tag: (id: string): UndoTarget => ({ kind: 'tag', id }),
}

/** The fields undo keeps of each kind of item. */
export const snap = {
  card: (n: CardDetail): Snapshot => ({
    title: n.title,
    color: n.color,
    tags: n.tags,
    assigneeIds: n.assignees.map((a) => a.principalId),
    agentWorking: n.agentWorking,
    x: n.x,
    y: n.y,
    bucketId: n.bucketId,
    components: n.components,
  }),
  component: (b: CardComponent): Snapshot => ({
    kind: b.kind,
    title: b.title,
    text: b.text,
    items: b.items,
    links: b.links,
    x: b.x,
    y: b.y,
    width: b.width,
    height: b.height,
  }),
  bucket: (b: BucketDetail): Snapshot => ({ name: b.name, x: b.x, y: b.y, width: b.width, height: b.height, collapsed: b.collapsed }),
  image: (i: ImageDetail): Snapshot => ({ cardId: i.cardId, x: i.x, y: i.y, bucketId: i.bucketId, width: i.width, caption: i.caption }),
  drawing: (d: DrawingDetail): Snapshot => ({ cardId: d.cardId, x: d.x, y: d.y, bucketId: d.bucketId, colors: d.colors }),
  /** A drawing's corner and strokes as loaded here (strokes null when they aren't loaded, as after someone else changed them). */
  shape: (d: DrawingDetail, strokes = loadedStrokes(d)): Snapshot => ({ x: d.x, y: d.y, strokes }),
  /** A stroke, if the drawing has it: whether it is still the drawing's last one (undo only takes off the last). */
  stroke: (d: DrawingDetail, index: number): Snapshot | null => (d.colors.length > index ? { last: d.colors.length === index + 1 } : null),
  tag: (t: BoardTag): Snapshot => ({ name: t.name, color: t.color }),
}

/** The step for a drawing scaled or turned: its corner and strokes before and after; null when nothing changed. */
export function reshaped(label: string, before: DrawingDetail, beforeStrokes: DrawingStroke[], after: DrawingDetail, afterStrokes: DrawingStroke[]) {
  const from = snap.shape(before, beforeStrokes)
  const to = snap.shape(after, afterStrokes)
  return same(from, to) ? null : undoEntry(label, [{ target: target.shape(before.id), before: from, after: to }])
}

const pick = (from: Snapshot, keys: string[]): Snapshot => Object.fromEntries(keys.map((k) => [k, from[k]]))

export const created = (t: UndoTarget, after: Snapshot): UndoChange => ({ target: t, before: null, after })
export const deleted = (t: UndoTarget, before: Snapshot): UndoChange => ({ target: t, before, after: null })
/** A change of some fields; nothing (null) when none of them changed. A position keeps x and y together. */
export function patched(t: UndoTarget, before: Snapshot, after: Snapshot, keys: string[], implied = false): UndoChange | null {
  let changed = keys.filter((k) => !same(before[k], after[k]))
  // An item's bucket goes with its place: where buckets overlap, or after a drop that went by the pointer, the place
  // alone doesn't say which one it was in.
  const place: string[] = [...keys.filter((k) => k === 'x' || k === 'y'), ...(t.kind === 'card' || t.kind === 'image' || t.kind === 'drawing' ? ['bucketId'] : [])]
  if (changed.includes('x') || changed.includes('y')) changed = [...new Set([...changed, ...place])]
  return changed.length === 0 ? null : { target: t, before: pick(before, changed), after: pick(after, changed), implied }
}

/** A step from changes, or null when nothing changed. */
export function step(label: string, changes: (UndoChange | null)[], options?: { coalesce?: string; toast?: string }): UndoEntry | null {
  const real = changes.filter((c): c is UndoChange => c !== null)
  return real.length === 0 ? null : undoEntry(label, real, options)
}

const cardLabels: Record<keyof CardChanges, string> = {
  title: 'Edit title',
  color: 'Change color',
  tags: 'Change tags',
  assigneeIds: 'Change people',
  agentWorking: 'Clear agent halo',
}

/** The step for an edit of a card's fields (updateCard). Picking tags or people one by one makes one step. */
export function cardEdit(before: CardDetail, after: CardDetail, changes: CardChanges): UndoEntry | null {
  const keys = Object.keys(changes) as (keyof CardChanges)[]
  const list = keys.length === 1 && (keys[0] === 'tags' || keys[0] === 'assigneeIds') ? keys[0] : undefined
  return step(keys.length === 1 ? cardLabels[keys[0]] : 'Edit card', [patched(target.card(before.id), snap.card(before), snap.card(after), keys)], {
    coalesce: list && `card:${before.id}:${list}`,
  })
}

const componentLabels: Record<string, string> = { title: 'Edit component title', text: 'Edit text', items: 'Edit checklist', links: 'Edit links' }

/** The step for an edit of a component's content (updateCardComponent). */
export function componentEdit(cardId: string, before: CardComponent, after: CardComponent, changes: CardComponentChanges): UndoEntry | null {
  const keys = Object.keys(changes)
  const label = keys.length === 1 && componentLabels[keys[0]] ? componentLabels[keys[0]] : 'Edit component'
  return step(label, [patched(target.component(cardId, before.id), snap.component(before), snap.component(after), keys)])
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)

/** An item's fields as they are on `board` now; null when it isn't there. */
function read(board: BoardDetail, t: UndoTarget): Snapshot | null {
  switch (t.kind) {
    case 'card': {
      const card = board.cards.find((n) => n.id === t.id)
      return card ? snap.card(card) : null
    }
    case 'component': {
      const component = board.cards.find((n) => n.id === t.cardId)?.components.find((b) => b.id === t.id)
      return component ? snap.component(component) : null
    }
    case 'bucket': {
      const bucket = board.buckets.find((b) => b.id === t.id)
      return bucket ? snap.bucket(bucket) : null
    }
    case 'image': {
      const image = board.images.find((i) => i.id === t.id)
      return image ? snap.image(image) : null
    }
    case 'drawing': {
      const drawing = board.drawings.find((d) => d.id === t.id)
      return drawing ? snap.drawing(drawing) : null
    }
    case 'shape': {
      const drawing = board.drawings.find((d) => d.id === t.id)
      return drawing ? snap.shape(drawing) : null
    }
    case 'stroke': {
      const drawing = board.drawings.find((d) => d.id === t.id)
      return drawing ? snap.stroke(drawing, t.index) : null
    }
    case 'tag': {
      const tag = board.tags.find((x) => x.id === t.id)
      return tag ? snap.tag(tag) : null
    }
  }
}

const noun: Record<UndoTarget['kind'], string> = {
  card: 'the card',
  component: 'the component',
  bucket: 'the bucket',
  image: 'the image',
  drawing: 'the drawing',
  shape: 'the drawing',
  stroke: 'the line',
  tag: 'the tag',
}

/** The other boards a step's items are on, which are loaded with its own board before the step is checked and applied. */
export const otherBoards = (entry: UndoEntry, boardId: string) =>
  [...new Set(entry.changes.flatMap((c) => (c.boardId && c.boardId !== boardId ? [c.boardId] : [])))]

/**
 * Why the step can't be applied to `home` (and the `elsewhere` boards its items may be on) as they are now, or null
 * when it can. `moving` tells whether someone else is dragging an item right now.
 */
export function check(
  entry: UndoEntry,
  direction: UndoDirection,
  home: BoardDetail,
  moving: (id: string) => boolean,
  elsewhere: Record<string, BoardDetail> = {},
): string | null {
  for (const change of entry.changes) {
    const board = change.boardId && change.boardId !== home.id ? elsewhere[change.boardId] : home
    if (!board) return 'the other board is no longer open to you'
    const t = change.target
    const expected = direction === 'undo' ? change.after : change.before
    const goal = direction === 'undo' ? change.before : change.after
    const now = read(board, t)
    if (t.kind === 'component' && goal && !board.cards.some((n) => n.id === t.cardId)) return 'the card was deleted since'
    if (t.kind === 'stroke' && !board.drawings.some((d) => d.id === t.id)) return 'the drawing was deleted since'
    if (expected === null) {
      if (now !== null) return `${noun[t.kind]} is back already`
    } else {
      if (now === null) return `${noun[t.kind]} was deleted since`
      if (Object.keys(expected).some((k) => !same(now[k], expected[k]))) return `${noun[t.kind]} was changed since`
    }
    if (moving(t.id)) return `someone is moving ${noun[t.kind]} right now`
    if (!goal) continue
    if (t.kind === 'card' && Array.isArray(goal.assigneeIds)) {
      const had = (now?.assigneeIds as string[] | undefined) ?? []
      const gone = (goal.assigneeIds as string[]).filter((id) => !had.includes(id) && !board.people.some((p) => p.principalId === id))
      if (gone.length > 0) return 'someone on it no longer has access to the board'
    }
    if (t.kind === 'tag' && typeof goal.name === 'string' && board.tags.some((x) => x.id !== t.id && x.name === goal.name)) {
      return `a tag called “${goal.name}” exists now`
    }
    if ((t.kind === 'image' || t.kind === 'drawing') && typeof goal.cardId === 'string' && !board.cards.some((n) => n.id === goal.cardId)) {
      return 'its card was deleted since'
    }
  }
  return null
}

/** Applies the step: writes the old values back (undo) or the new ones again (redo). */
export async function apply(entry: UndoEntry, direction: UndoDirection, home: BoardDetail, elsewhere: Record<string, BoardDetail> = {}) {
  const changes = direction === 'undo' ? entry.changes.toReversed() : entry.changes
  for (const change of changes) {
    if (change.implied) continue
    const from = direction === 'undo' ? change.after : change.before
    const to = direction === 'undo' ? change.before : change.after
    await write(change.target, from, to, (change.boardId && elsewhere[change.boardId]) || home)
    if (change.target.kind === 'bucket' && from && to) await fixCarried(entry, direction, change.target.carry, from, to)
  }
}

/** Text fields are cleared with an empty string. */
const text = (value: unknown) => (value ?? '') as string

/** The bucket a recorded place had, sent back as it was (a drop by the pointer may have been loose over a bucket). */
const dropBucket = (bucketId: unknown): DropBucket =>
  typeof bucketId === 'string' ? { bucketId } : bucketId === null ? { loose: true } : {}

/** Whether an image or drawing ends up on the board, not on a card: as `to` puts it, or else as it is now. */
const endsOnBoard = (id: string, items: { id: string; cardId: string | null }[], to: Snapshot) =>
  ('cardId' in to ? to.cardId : items.find((i) => i.id === id)?.cardId) == null

async function write(t: UndoTarget, from: Snapshot | null, to: Snapshot | null, board: BoardDetail) {
  const boardId = board.id
  if (t.kind === 'stroke') {
    if (to === null) {
      await api.deleteDrawingStroke(t.id, t.index)
    } else if (from === null) {
      // check() made sure the drawing is there.
      const drawing = board.drawings.find((d) => d.id === t.id)!
      await api.addDrawingStroke(t.id, { ...t.stroke, x: drawing.x + t.stroke.x, y: drawing.y + t.stroke.y })
    }
    return
  }
  if (t.kind === 'shape') {
    if (!from || !to) return
    const after = await api.setDrawingStrokes(t.id, { x: to.x as number, y: to.y as number, strokes: to.strokes as DrawingStroke[] })
    // Loaded now, so the next step's check can compare them.
    await loadDrawingStrokes(after)
    return
  }
  if (to === null) {
    if (t.kind === 'card') await api.deleteCard(t.id)
    else if (t.kind === 'component') await api.deleteCardComponent(t.cardId, t.id)
    else if (t.kind === 'bucket') await api.deleteBucket(t.id)
    else if (t.kind === 'image') await api.deleteImage(t.id)
    else if (t.kind === 'drawing') await api.deleteDrawing(t.id)
    else await api.deleteBoardTag(boardId, t.id)
    return
  }
  if (from === null) {
    if (t.kind === 'card') await api.restoreCard(t.id)
    else if (t.kind === 'component') {
      const b = to as unknown as CardComponent
      await api.addCardComponent(t.cardId, { id: t.id, kind: b.kind, title: b.title, text: b.text, items: b.items, links: b.links, x: b.x, y: b.y, width: b.width, height: b.height })
    } else if (t.kind === 'bucket') await api.restoreBucket(t.id)
    else if (t.kind === 'image') await api.restoreImage(t.id)
    else if (t.kind === 'drawing') await api.restoreDrawing(t.id)
    else await api.restoreBoardTag(boardId, t.id)
    return
  }
  switch (t.kind) {
    case 'card': {
      const { x, y, bucketId, components: _components, ...rest } = to
      if (x !== undefined) await api.moveCard(t.id, { x: x as number, y: y as number, ...dropBucket(bucketId) })
      if (Object.keys(rest).length > 0) await api.updateCard(t.id, rest as CardChanges)
      return
    }
    case 'component': {
      const changes = { ...to } as CardComponentChanges
      if ('title' in to) changes.title = text(to.title)
      if ('text' in to) changes.text = text(to.text)
      await api.updateCardComponent(t.cardId, t.id, changes)
      return
    }
    case 'bucket': {
      const at = (key: string) => to[key] as number | undefined
      if ('width' in to || 'height' in to) {
        // x/y sent with a size is a resize, which leaves the bucket's items where they are.
        await api.updateBucket(t.id, { x: at('x'), y: at('y'), width: at('width'), height: at('height') })
      } else if ('x' in to) {
        await api.updateBucket(t.id, {
          x: at('x'),
          y: at('y'),
          carryCardIds: t.carry?.cards ?? [],
          carryImageIds: t.carry?.images ?? [],
          carryDrawingIds: t.carry?.drawings ?? [],
        })
      }
      if ('name' in to) await api.updateBucket(t.id, { name: to.name as string })
      if ('collapsed' in to) await api.updateBucket(t.id, { collapsed: to.collapsed as boolean })
      return
    }
    case 'image': {
      const changes: ImageChanges = {}
      if ('cardId' in to) {
        if (to.cardId) changes.cardId = to.cardId as string
        else changes.detach = true
      }
      if ('x' in to) Object.assign(changes, { x: to.x, y: to.y }, endsOnBoard(t.id, board.images, to) ? dropBucket(to.bucketId) : {})
      if ('width' in to) changes.width = to.width as number
      if ('caption' in to) changes.caption = text(to.caption)
      await api.updateImage(t.id, changes)
      return
    }
    case 'drawing': {
      const changes: DrawingChanges = {}
      if ('cardId' in to) {
        if (to.cardId) changes.cardId = to.cardId as string
        else changes.detach = true
      }
      if ('x' in to) Object.assign(changes, { x: to.x, y: to.y }, endsOnBoard(t.id, board.drawings, to) ? dropBucket(to.bucketId) : {})
      if ('colors' in to) changes.colors = to.colors as DrawingChanges['colors']
      await api.updateDrawing(t.id, changes)
      return
    }
    case 'tag':
      await api.updateBoardTag(boardId, t.id, to as TagChanges)
  }
}

/**
 * A moved bucket carries its cards, images and drawings by the same distance, but never past the edge of the coordinate
 * range, so an item stopped at the edge doesn't come back exactly when the bucket moves back. This puts such items
 * where the step says they belong. It only touches what this bucket carried: a step that moved several buckets lists
 * the others' items too.
 */
async function fixCarried(entry: UndoEntry, direction: UndoDirection, carry: BucketCarry | undefined, from: Snapshot, to: Snapshot) {
  if (!('x' in to) || 'width' in to || 'height' in to) return
  const dx = (to.x as number) - (from.x as number)
  const dy = (to.y as number) - (from.y as number)
  const carried = new Set([...(carry?.cards ?? []), ...(carry?.images ?? []), ...(carry?.drawings ?? [])])
  for (const change of entry.changes) {
    const kind = change.target.kind
    if (!change.implied || (kind !== 'card' && kind !== 'image' && kind !== 'drawing') || !carried.has(change.target.id)) continue
    const was = direction === 'undo' ? change.after : change.before
    const goal = direction === 'undo' ? change.before : change.after
    if (!was || !goal) continue
    const landed = { x: onBoard((was.x as number) + dx), y: onBoard((was.y as number) + dy) }
    if (landed.x === goal.x && landed.y === goal.y) continue
    const place = { x: goal.x as number, y: goal.y as number }
    if (kind === 'card') await api.moveCard(change.target.id, place)
    else if (kind === 'image') await api.updateImage(change.target.id, place)
    else await api.updateDrawing(change.target.id, place)
  }
}
