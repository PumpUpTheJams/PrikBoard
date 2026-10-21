import { api } from '../api/client'
import type {
  BoardTag,
  CardChanges,
  CardColor,
  CardComponent,
  CardComponentChanges,
  CardDetail,
  ImageChanges,
  ImageDetail,
  NewCardComponent,
  TagColor,
} from '../api/types'
import { cardEdit, componentEdit, created, deleted, patched, snap, step, target, type UndoEntry } from './undo'

/**
 * A change to the board as a request and the undo step it makes from the result, to hand to a RecordingRun (or the
 * board's `settle`) spread out: `run(...ops.editCard(card, { title }))`. The board canvas, the card's canvas and the
 * mobile layout make the same changes through these, so they are recorded for undo the same way everywhere.
 */
export type Op<T> = [action: () => Promise<T>, describe: (result: T) => UndoEntry | null]

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
