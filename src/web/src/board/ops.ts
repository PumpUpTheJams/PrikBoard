import { api } from '../api/client'
import type {
  BoardClip,
  BoardTag,
  CardChanges,
  CardColor,
  CardComponent,
  CardComponentChanges,
  CardDetail,
  DrawingDetail,
  DrawingStroke,
  ImageChanges,
  ImageDetail,
  NewCardComponent,
  PastedItems,
  PastePlace,
  TagColor,
} from '../api/types'
import { countList } from './clipboard'
import { loadDrawingStrokes, type Reshape } from './drawings'
import { cardEdit, componentEdit, created, deleted, patched, reshaped, snap, step, target, type UndoChange, type UndoEntry } from './undo'

/**
 * A change to the board as a request and the undo step it makes from the result, to hand to a RecordingRun (or the
 * board's `settle`) spread out: `run(...ops.editCard(card, { title }))`. The board canvas, the card's canvas and the
 * mobile layout make the same changes through these, so they are recorded for undo the same way everywhere.
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

/** Whether a component has nothing in it: removing it then needs no toast to undo it (it still can be). */
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
   * Adds copied items (a clip) to the board as one step; undo deletes the buckets, cards, and images and drawings on the
   * board it added (the images and drawings on the cards go with them).
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
