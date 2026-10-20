import { api } from '../api/client'
import type {
  BoardDetail,
  BoardTag,
  BucketDetail,
  ImageChanges,
  ImageDetail,
  NoteBlock,
  NoteBlockChanges,
  NoteChanges,
  NoteDetail,
  TagChanges,
} from '../api/types'
import { onBoard } from './layout'

/**
 * Undo and redo for changes made in this tab. Each change is recorded as what it touched before and after (the fields
 * it changed, or the whole item when it was created or deleted). Undoing first checks a fresh board: if any of those
 * fields changed since (someone else, an agent, another tab), the step is refused with the reason. Otherwise the old
 * values are written back; deleted items come back through the restore endpoints, with the same ids.
 */

/** Some fields of an item, as the API returns them. */
export type Snapshot = Record<string, unknown>

export type UndoTarget =
  | { kind: 'note'; id: string }
  | { kind: 'block'; noteId: string; id: string }
  /** `carry`: for a move, the post-its and images it took along. */
  | { kind: 'bucket'; id: string; carry?: BucketCarry }
  | { kind: 'image'; id: string }
  | { kind: 'tag'; id: string }

/** What a moved bucket took along: post-its, and images on the board. */
export interface BucketCarry {
  notes: string[]
  images: string[]
}

/** One item's part of a step: null `before` means it was created, null `after` that it was deleted. */
export interface UndoChange {
  target: UndoTarget
  before: Snapshot | null
  after: Snapshot | null
  /** Changed as a side effect (post-its carried by a bucket): checked, but written by the bucket's move. */
  implied?: boolean
}

export interface UndoEntry {
  id: number
  /** What the step did, e.g. "Move post-it". */
  label: string
  changes: UndoChange[]
  /** Consecutive steps with the same key become one (picking several tags in a row). */
  coalesce?: string
  /** Shown as a toast with an Undo button when recorded (deletes, which no longer ask first). */
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
  note: (id: string): UndoTarget => ({ kind: 'note', id }),
  block: (noteId: string, id: string): UndoTarget => ({ kind: 'block', noteId, id }),
  bucket: (id: string, carry?: BucketCarry): UndoTarget => ({ kind: 'bucket', id, carry }),
  image: (id: string): UndoTarget => ({ kind: 'image', id }),
  tag: (id: string): UndoTarget => ({ kind: 'tag', id }),
}

/** The fields undo keeps of each kind of item. */
export const snap = {
  note: (n: NoteDetail): Snapshot => ({
    title: n.title,
    color: n.color,
    tags: n.tags,
    assigneeIds: n.assignees.map((a) => a.principalId),
    agentWorking: n.agentWorking,
    x: n.x,
    y: n.y,
    blocks: n.blocks,
  }),
  block: (b: NoteBlock): Snapshot => ({
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
  bucket: (b: BucketDetail): Snapshot => ({ name: b.name, x: b.x, y: b.y, width: b.width, height: b.height }),
  image: (i: ImageDetail): Snapshot => ({ noteId: i.noteId, x: i.x, y: i.y, width: i.width, caption: i.caption }),
  tag: (t: BoardTag): Snapshot => ({ name: t.name, color: t.color }),
}

const pick = (from: Snapshot, keys: string[]): Snapshot => Object.fromEntries(keys.map((k) => [k, from[k]]))

export const created = (t: UndoTarget, after: Snapshot): UndoChange => ({ target: t, before: null, after })
export const deleted = (t: UndoTarget, before: Snapshot): UndoChange => ({ target: t, before, after: null })
/** A change of some fields; nothing (null) when none of them changed. A position keeps x and y together. */
export function patched(t: UndoTarget, before: Snapshot, after: Snapshot, keys: string[], implied = false): UndoChange | null {
  let changed = keys.filter((k) => !same(before[k], after[k]))
  if (changed.includes('x') || changed.includes('y')) changed = [...new Set([...changed, ...keys.filter((k) => k === 'x' || k === 'y')])]
  return changed.length === 0 ? null : { target: t, before: pick(before, changed), after: pick(after, changed), implied }
}

/** A step from changes, or null when nothing changed. */
export function step(label: string, changes: (UndoChange | null)[], options?: { coalesce?: string; toast?: string }): UndoEntry | null {
  const real = changes.filter((c): c is UndoChange => c !== null)
  return real.length === 0 ? null : undoEntry(label, real, options)
}

const noteLabels: Record<keyof NoteChanges, string> = {
  title: 'Edit title',
  color: 'Change colour',
  tags: 'Change tags',
  assigneeIds: 'Change people',
  agentWorking: 'Clear agent halo',
}

/** The step for an edit of a post-it's fields (updateNote). Picking tags or people one by one makes one step. */
export function noteEdit(before: NoteDetail, after: NoteDetail, changes: NoteChanges): UndoEntry | null {
  const keys = Object.keys(changes) as (keyof NoteChanges)[]
  const list = keys.length === 1 && (keys[0] === 'tags' || keys[0] === 'assigneeIds') ? keys[0] : undefined
  return step(keys.length === 1 ? noteLabels[keys[0]] : 'Edit post-it', [patched(target.note(before.id), snap.note(before), snap.note(after), keys)], {
    coalesce: list && `note:${before.id}:${list}`,
  })
}

const blockLabels: Record<string, string> = { title: 'Edit block title', text: 'Edit text', items: 'Edit checklist', links: 'Edit links' }

/** The step for an edit of a block's content (updateNoteBlock). */
export function blockEdit(noteId: string, before: NoteBlock, after: NoteBlock, changes: NoteBlockChanges): UndoEntry | null {
  const keys = Object.keys(changes)
  const label = keys.length === 1 && blockLabels[keys[0]] ? blockLabels[keys[0]] : 'Edit block'
  return step(label, [patched(target.block(noteId, before.id), snap.block(before), snap.block(after), keys)])
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)

/** An item's fields as they are on `board` now; null when it isn't there. */
function read(board: BoardDetail, t: UndoTarget): Snapshot | null {
  switch (t.kind) {
    case 'note': {
      const note = board.notes.find((n) => n.id === t.id)
      return note ? snap.note(note) : null
    }
    case 'block': {
      const block = board.notes.find((n) => n.id === t.noteId)?.blocks.find((b) => b.id === t.id)
      return block ? snap.block(block) : null
    }
    case 'bucket': {
      const bucket = board.buckets.find((b) => b.id === t.id)
      return bucket ? snap.bucket(bucket) : null
    }
    case 'image': {
      const image = board.images.find((i) => i.id === t.id)
      return image ? snap.image(image) : null
    }
    case 'tag': {
      const tag = board.tags.find((x) => x.id === t.id)
      return tag ? snap.tag(tag) : null
    }
  }
}

const noun: Record<UndoTarget['kind'], string> = { note: 'the post-it', block: 'the block', bucket: 'the bucket', image: 'the image', tag: 'the tag' }

/**
 * Why the step can't be taken on `board` as it is now, or null when it can. `moving` tells whether someone else is
 * dragging an item right now.
 */
export function check(entry: UndoEntry, direction: UndoDirection, board: BoardDetail, moving: (id: string) => boolean): string | null {
  for (const change of entry.changes) {
    const t = change.target
    const expected = direction === 'undo' ? change.after : change.before
    const goal = direction === 'undo' ? change.before : change.after
    const now = read(board, t)
    if (t.kind === 'block' && goal && !board.notes.some((n) => n.id === t.noteId)) return 'the post-it was deleted since'
    if (expected === null) {
      if (now !== null) return `${noun[t.kind]} is back already`
    } else {
      if (now === null) return `${noun[t.kind]} was deleted since`
      if (Object.keys(expected).some((k) => !same(now[k], expected[k]))) return `${noun[t.kind]} was changed since`
    }
    if (moving(t.id)) return `someone is moving ${noun[t.kind]} right now`
    if (!goal) continue
    if (t.kind === 'note' && Array.isArray(goal.assigneeIds)) {
      const had = (now?.assigneeIds as string[] | undefined) ?? []
      const gone = (goal.assigneeIds as string[]).filter((id) => !had.includes(id) && !board.people.some((p) => p.principalId === id))
      if (gone.length > 0) return 'someone on it no longer has access to the board'
    }
    if (t.kind === 'tag' && typeof goal.name === 'string' && board.tags.some((x) => x.id !== t.id && x.name === goal.name)) {
      return `a tag called “${goal.name}” exists now`
    }
    if (t.kind === 'image' && typeof goal.noteId === 'string' && !board.notes.some((n) => n.id === goal.noteId)) {
      return 'its post-it was deleted since'
    }
  }
  return null
}

/** Takes the step: writes the old values back (undo) or the new ones again (redo). */
export async function apply(entry: UndoEntry, direction: UndoDirection, boardId: string) {
  const changes = direction === 'undo' ? entry.changes.toReversed() : entry.changes
  for (const change of changes) {
    if (change.implied) continue
    const from = direction === 'undo' ? change.after : change.before
    const to = direction === 'undo' ? change.before : change.after
    await write(change.target, from, to, boardId)
    if (change.target.kind === 'bucket' && from && to) await fixCarried(entry, direction, from, to)
  }
}

/** Text fields are cleared with an empty string. */
const text = (value: unknown) => (value ?? '') as string

async function write(t: UndoTarget, from: Snapshot | null, to: Snapshot | null, boardId: string) {
  if (to === null) {
    if (t.kind === 'note') await api.deleteNote(t.id)
    else if (t.kind === 'block') await api.deleteNoteBlock(t.noteId, t.id)
    else if (t.kind === 'bucket') await api.deleteBucket(t.id)
    else if (t.kind === 'image') await api.deleteImage(t.id)
    else await api.deleteBoardTag(boardId, t.id)
    return
  }
  if (from === null) {
    if (t.kind === 'note') await api.restoreNote(t.id)
    else if (t.kind === 'block') {
      const b = to as unknown as NoteBlock
      await api.addNoteBlock(t.noteId, { id: t.id, kind: b.kind, title: b.title, text: b.text, items: b.items, links: b.links, x: b.x, y: b.y, width: b.width, height: b.height })
    } else if (t.kind === 'bucket') await api.restoreBucket(t.id)
    else if (t.kind === 'image') await api.restoreImage(t.id)
    else await api.restoreBoardTag(boardId, t.id)
    return
  }
  switch (t.kind) {
    case 'note': {
      const { x, y, blocks: _blocks, ...rest } = to
      if (x !== undefined) await api.moveNote(t.id, { x: x as number, y: y as number })
      if (Object.keys(rest).length > 0) await api.updateNote(t.id, rest as NoteChanges)
      return
    }
    case 'block': {
      const changes = { ...to } as NoteBlockChanges
      if ('title' in to) changes.title = text(to.title)
      if ('text' in to) changes.text = text(to.text)
      await api.updateNoteBlock(t.noteId, t.id, changes)
      return
    }
    case 'bucket': {
      const at = (key: string) => to[key] as number | undefined
      if ('width' in to || 'height' in to) {
        // A new place with a new size is a resize: the post-its stay.
        await api.updateBucket(t.id, { x: at('x'), y: at('y'), width: at('width'), height: at('height') })
      } else if ('x' in to) {
        await api.updateBucket(t.id, { x: at('x'), y: at('y'), carryNoteIds: t.carry?.notes ?? [], carryImageIds: t.carry?.images ?? [] })
      }
      if ('name' in to) await api.updateBucket(t.id, { name: to.name as string })
      return
    }
    case 'image': {
      const changes: ImageChanges = {}
      if ('noteId' in to) {
        if (to.noteId) changes.noteId = to.noteId as string
        else changes.detach = true
      }
      if ('x' in to) Object.assign(changes, { x: to.x, y: to.y })
      if ('width' in to) changes.width = to.width as number
      if ('caption' in to) changes.caption = text(to.caption)
      await api.updateImage(t.id, changes)
      return
    }
    case 'tag':
      await api.updateBoardTag(boardId, t.id, to as TagChanges)
  }
}

/**
 * A moved bucket carries its post-its and images by the same distance, but never past the end of the coordinate range.
 * One that was stopped there on the way doesn't come back exactly, so it is put where it belongs.
 */
async function fixCarried(entry: UndoEntry, direction: UndoDirection, from: Snapshot, to: Snapshot) {
  if (!('x' in to) || 'width' in to || 'height' in to) return
  const dx = (to.x as number) - (from.x as number)
  const dy = (to.y as number) - (from.y as number)
  for (const change of entry.changes) {
    const kind = change.target.kind
    if (!change.implied || (kind !== 'note' && kind !== 'image')) continue
    const was = direction === 'undo' ? change.after : change.before
    const goal = direction === 'undo' ? change.before : change.after
    if (!was || !goal) continue
    const landed = { x: onBoard((was.x as number) + dx), y: onBoard((was.y as number) + dy) }
    if (landed.x === goal.x && landed.y === goal.y) continue
    const place = { x: goal.x as number, y: goal.y as number }
    if (kind === 'note') await api.moveNote(change.target.id, place)
    else await api.updateImage(change.target.id, place)
  }
}
