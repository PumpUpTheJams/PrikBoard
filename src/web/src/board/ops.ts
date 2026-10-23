import { api } from '../api/client'
import type {
  BoardClip,
  BoardDetail,
  BoardTag,
  BucketDetail,
  CardChanges,
  CardColor,
  CardComponent,
  CardComponentChanges,
  CardDetail,
  DrawingChanges,
  DrawingDetail,
  DrawingStroke,
  ImageChanges,
  ImageDetail,
  NewCardComponent,
  PastedItems,
  PastePlace,
  Point,
  TagColor,
} from '../api/types'
import { countList } from './clipboard'
import { loadDrawingStrokes, type Reshape } from './drawings'
import { onBoard } from './layout'
import { cardEdit, componentEdit, created, deleted, patched, reshaped, snap, step, target, type RecordingRun, type UndoChange, type UndoEntry } from './undo'

/**
 * A change: its request, and a function that builds its undo step from the result. Spread it into a RecordingRun,
 * `run(...ops.editCard(card, { title }))`; every layout changes things through these, so undo records them alike.
 */
export type Op<T> = [action: () => Promise<T>, describe: (result: T) => UndoEntry | null]

/** What a paste added, for its toast: "1 bucket, 3 cards and 2 images" (images and drawings on the cards not counted). */
export const pastedSummary = (pasted: PastedItems) =>
  countList([
    [pasted.buckets.length, 'bucket'],
    [pasted.cards.length, 'card'],
    [pasted.images.filter((i) => !i.cardId).length, 'image'],
    [pasted.drawings.filter((d) => !d.cardId).length, 'drawing'],
  ])

/** A change that failed partway: `done` is the result so far, which a recording run still records before it fails. */
export class PartlyDone<T> extends Error {
  readonly done: T
  readonly failure: unknown
  constructor(done: T, failure: unknown) {
    super('Partly done')
    this.done = done
    this.failure = failure
  }
}

/**
 * A RecordingRun on top of the board's `run`: runs the change, then records the step `describe` builds from its result.
 * A change that throws PartlyDone records the step for what went through, then fails as usual.
 */
export function recordingRun(
  run: (action: () => Promise<unknown>) => Promise<boolean>,
  undo: { track: (promise: Promise<boolean>) => Promise<boolean>; record: (entry: UndoEntry) => void },
): RecordingRun {
  return (action, describe) =>
    undo.track(
      run(async () => {
        const record = (result: Parameters<NonNullable<typeof describe>>[0]) => {
          const entry = describe?.(result)
          if (entry) undo.record(entry)
        }
        try {
          record(await action())
        } catch (error) {
          if (!(error instanceof PartlyDone)) throw error
          record(error.done)
          throw error.failure
        }
      }),
    )
}

/** A RecordingRun that calls `done` once the change has gone through or failed (the canvases let go of a finished gesture there). */
export const settling =
  (run: RecordingRun, done: () => void): RecordingRun =>
  async (action, describe) => {
    const ok = await run(action, describe)
    done()
    return ok
  }

/**
 * Runs a change made of several requests as one step (`label`, by its changes when it is a function). `work` pushes
 * the change of each request that went through; when one fails, the step keeps those.
 */
export function runAll(
  run: RecordingRun,
  label: string | ((changes: (UndoChange | null)[]) => string),
  toast: string | undefined,
  work: (changes: (UndoChange | null)[]) => Promise<unknown>,
): Promise<boolean> {
  return run(
    async () => {
      const changes: (UndoChange | null)[] = []
      try {
        await work(changes)
      } catch (error) {
        throw new PartlyDone(changes, error)
      }
      return changes
    },
    (changes) => step(typeof label === 'string' ? label : label(changes), changes, { toast }),
  )
}

/** Waits for all of them, then fails with the first failure, if any (the ones that worked still count). */
export async function allOf(requests: Promise<unknown>[]) {
  const failed = (await Promise.allSettled(requests)).find((r) => r.status === 'rejected')
  if (failed) throw failed.reason
}

/**
 * The step's changes for a bucket that moved: the bucket, and the cards, images and drawings it carried along by the
 * same distance (undoing the move takes exactly those back).
 */
export function bucketMove(board: BoardDetail, bucket: BucketDetail, moved: BucketDetail): (UndoChange | null)[] {
  const carried = board.cards.filter((n) => n.bucketId === bucket.id)
  const carriedImages = board.images.filter((i) => i.bucketId === bucket.id)
  const carriedDrawings = board.drawings.filter((d) => d.bucketId === bucket.id)
  const d = { x: moved.x - bucket.x, y: moved.y - bucket.y }
  const along = (item: Point) => ({ x: onBoard(item.x + d.x), y: onBoard(item.y + d.y) })
  const carry = { cards: carried.map((n) => n.id), images: carriedImages.map((i) => i.id), drawings: carriedDrawings.map((d) => d.id) }
  return [
    patched(target.bucket(bucket.id, carry), snap.bucket(bucket), snap.bucket(moved), ['x', 'y']),
    // A card's place always comes with its bucket (see `patched`), and a carried card stays in this one.
    ...carried.map((n) => patched(target.card(n.id), { x: n.x, y: n.y, bucketId: n.bucketId }, { ...along(n), bucketId: n.bucketId }, ['x', 'y'], true)),
    ...carriedImages.map((i) => patched(target.image(i.id), { x: i.x, y: i.y }, along(i), ['x', 'y'], true)),
    ...carriedDrawings.map((dr) => patched(target.drawing(dr.id), { x: dr.x, y: dr.y }, along(dr), ['x', 'y'], true)),
  ]
}

/** Whether a component is empty. Removing an empty one shows no undo toast, though it can still be undone. */
export const isEmptyComponent = (c: CardComponent) => !c.text && !c.items?.length && !c.links?.length && !c.title

export const ops = {
  /** A new card in a bucket (the server finds it a free spot, growing a full bucket), or at a point on the board. */
  addCard: (boardId: string, place: { bucketId: string } | { x: number; y: number }, color?: CardColor): Op<CardDetail> => [
    () => api.createCard(boardId, { title: '', color, ...place }),
    (card) => step('Add card', [created(target.card(card.id), snap.card(card))]),
  ],

  /** Edits a card's fields as one step. */
  editCard: (card: CardDetail, changes: CardChanges): Op<CardDetail> => [
    () => api.updateCard(card.id, changes),
    (after) => cardEdit(card, after, changes),
  ],

  /**
   * Adds copied items (a clip) to the board as one step. Undo deletes the buckets, cards, images and drawings it added
   * (images and drawings on the pasted cards go with their cards).
   */
  paste: (boardId: string, clip: BoardClip, place: PastePlace, label = 'Paste'): Op<PastedItems> => [
    () => api.pasteItems(boardId, clip, place),
    (pasted) => {
      // A cut's originals went to the trash of the board they were cut from: undo puts them back there.
      const moved = pasted.moved
      const away = (change: UndoChange): UndoChange => ({ ...change, boardId: moved!.boardId })
      const originals = moved
        ? [
            ...moved.buckets.map((b) => away(deleted(target.bucket(b.id), snap.bucket(b)))),
            ...moved.cards.map((n) => away(deleted(target.card(n.id), snap.card(n)))),
            ...moved.images.map((i) => away(deleted(target.image(i.id), snap.image(i)))),
            ...moved.drawings.map((d) => away(deleted(target.drawing(d.id), snap.drawing(d)))),
          ]
        : []
      const toast = moved
        ? `Moved ${pastedSummary(pasted)} from ${moved.boardName}`
        : clip.cut
          ? `Pasted copies of ${pastedSummary(pasted)}: the originals stay on their server`
          : `Pasted ${pastedSummary(pasted)}`
      return step(
        moved ? 'Move here' : label,
        [
          ...pasted.buckets.map((b) => created(target.bucket(b.id), snap.bucket(b))),
          ...pasted.cards.map((card) => created(target.card(card.id), snap.card(card))),
          ...pasted.images.filter((i) => !i.cardId).map((i) => created(target.image(i.id), snap.image(i))),
          ...pasted.drawings.filter((d) => !d.cardId).map((d) => created(target.drawing(d.id), snap.drawing(d))),
          ...originals,
        ],
        label === 'Paste' ? { toast } : undefined,
      )
    },
  ],

  deleteCard: (card: CardDetail): Op<void> => [
    () => api.deleteCard(card.id),
    () => step('Delete card', [deleted(target.card(card.id), snap.card(card))], { toast: 'Card deleted' }),
  ],

  /** Moves a card into a bucket, in a free spot there; `toast` confirms it with an Undo button. */
  moveCardInto: (card: CardDetail, bucketId: string, toast?: string): Op<CardDetail> => [
    () => api.moveCard(card.id, { bucketId }),
    (moved) => step('Move card', [patched(target.card(card.id), snap.card(card), snap.card(moved), ['x', 'y'])], { toast }),
  ],

  addComponent: (cardId: string, component: NewCardComponent): Op<CardComponent> => [
    () => api.addCardComponent(cardId, component),
    (added) => step('Add component', [created(target.component(cardId, added.id), snap.component(added))]),
  ],

  editComponent: (cardId: string, component: CardComponent, changes: CardComponentChanges): Op<CardComponent> => [
    () => api.updateCardComponent(cardId, component.id, changes),
    (after) => componentEdit(cardId, component, after, changes),
  ],

  removeComponent: (cardId: string, component: CardComponent): Op<void> => [
    () => api.deleteCardComponent(cardId, component.id),
    () =>
      step('Remove component', [deleted(target.component(cardId, component.id), snap.component(component))], {
        toast: isEmptyComponent(component) ? undefined : 'Component removed',
      }),
  ],

  /** Changes an image (moves, resizes, recaptions or attaches it) as a step named `label` over the fields in `keys`. */
  editImage: (image: ImageDetail, changes: ImageChanges, label: string, keys: string[]): Op<ImageDetail> => [
    () => api.updateImage(image.id, changes),
    (after) => step(label, [patched(target.image(image.id), snap.image(image), snap.image(after), keys)]),
  ],

  /**
   * Scales and turns a drawing about a pivot. Its new strokes are loaded before the board reloads, so it doesn't blink;
   * undo puts the old ones back exactly.
   */
  reshapeDrawing: (drawing: DrawingDetail, { scaleX, scaleY, rotation, pivot }: Reshape): Op<{ after: DrawingDetail; before: DrawingStroke[]; strokes: DrawingStroke[] }> => [
    async () => {
      const before = await loadDrawingStrokes(drawing)
      const after = await api.transformDrawing(drawing.id, { scaleX, scaleY, rotation, pivotX: pivot.x, pivotY: pivot.y })
      return { after, before, strokes: await loadDrawingStrokes(after) }
    },
    ({ after, before, strokes }) => reshaped(rotation !== 0 ? 'Rotate drawing' : 'Resize drawing', drawing, before, after, strokes),
  ],

  deleteImage: (image: ImageDetail): Op<void> => [
    () => api.deleteImage(image.id),
    () => step('Delete image', [deleted(target.image(image.id), snap.image(image))], { toast: 'Image deleted' }),
  ],

  /** Changes a drawing (moves, recolors or attaches it) as a step named `label` over the fields in `keys`. */
  editDrawing: (drawing: DrawingDetail, changes: DrawingChanges, label: string, keys: string[]): Op<DrawingDetail> => [
    () => api.updateDrawing(drawing.id, changes),
    (after) => step(label, [patched(target.drawing(drawing.id), snap.drawing(drawing), snap.drawing(after), keys)]),
  ],

  deleteDrawing: (drawing: DrawingDetail): Op<void> => [
    () => api.deleteDrawing(drawing.id),
    () => step('Delete drawing', [deleted(target.drawing(drawing.id), snap.drawing(drawing))], { toast: 'Drawing deleted' }),
  ],

  renameBucket: (bucket: BucketDetail, name: string): Op<BucketDetail> => [
    () => api.updateBucket(bucket.id, { name }),
    (after) => step('Rename bucket', [patched(target.bucket(bucket.id), snap.bucket(bucket), snap.bucket(after), ['name'])]),
  ],

  /** Deletes a bucket; what is in it stays on the board. */
  deleteBucket: (bucket: BucketDetail): Op<void> => [
    () => api.deleteBucket(bucket.id),
    () => step('Delete bucket', [deleted(target.bucket(bucket.id), snap.bucket(bucket))], { toast: 'Bucket deleted' }),
  ],

  renameTag: (boardId: string, tag: BoardTag, name: string): Op<BoardTag> => [
    () => api.updateBoardTag(boardId, tag.id, { name }),
    (renamed) => step('Rename tag', [patched(target.tag(tag.id), snap.tag(tag), snap.tag(renamed), ['name'])]),
  ],

  recolorTag: (boardId: string, tag: BoardTag, color: TagColor): Op<BoardTag> => [
    () => api.updateBoardTag(boardId, tag.id, { color }),
    (recolored) => step('Change tag color', [patched(target.tag(tag.id), snap.tag(tag), snap.tag(recolored), ['color'])]),
  ],

  deleteTag: (boardId: string, tag: BoardTag): Op<void> => [
    () => api.deleteBoardTag(boardId, tag.id),
    () => step('Delete tag', [deleted(target.tag(tag.id), snap.tag(tag))], { toast: `Tag “${tag.name}” deleted` }),
  ],
}
