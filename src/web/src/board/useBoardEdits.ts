import { useState } from 'react'
import { api } from '../api/client'
import type { BoardDetail, BoardTag, BucketDetail, CardChanges, CardColor, CardDetail, DrawingDetail, InkColor, Point, Rect, TagColor } from '../api/types'
import { bucketOf, type Selection } from './boardItems'
import { pickImageFiles } from './files'
import { snapPoint } from './layout'
import { ops, runAll } from './ops'
import type { TagColorPreview } from './tagColors'
import type { Tool } from './tools'
import { created, patched, snap, step, target, type RecordingRun } from './undo'

/** Where a new image's top-left goes relative to the point it was dropped, pasted or added at. */
export const IMAGE_OFFSET = { x: 180, y: 120 }

interface BoardEditsOptions {
  board: BoardDetail
  /** Runs a change through the board, recorded for undo. */
  runRec: RecordingRun
  /** The same, letting go of the finished gesture afterwards. */
  settle: RecordingRun
  setSelection: (item: Selection | null) => void
  setSelected: (update: (list: Selection[]) => Selection[]) => void
  /** Takes an item out of the selection (it was deleted). */
  unselect: (id: string) => void
  /** The open card. */
  expanded: { id: string } | null
  setExpanded: (card: { id: string; fresh: boolean } | null) => void
  /** Brings a bucket to the front here (`server` false: the server puts it on top itself). */
  toFront: (id: string, server?: boolean) => void
  /** A card put at a point, naming the front bucket when it holds it there (`cardPlace`). */
  cardPlace: (at: Point) => Point & { bucketId?: string }
  /** The fill picked for new cards. */
  fill: CardColor
  setTool: (tool: Tool) => void
  /** The middle of what is on screen, in canvas pixels. */
  viewportCenter: () => Point
}

/** Changes to single items on the board, each one undo step (labels and toasts in ops). */
export function useBoardEdits(options: BoardEditsOptions) {
  const { board, runRec, settle, setSelection, setSelected, unselect, expanded, setExpanded, toFront, cardPlace, fill, setTool, viewportCenter } = options
  /** A tag color being saved: it shows on the tag everywhere until the reloaded board has it. */
  const [tagSaving, setTagSaving] = useState<TagColorPreview | null>(null)
  /** Image uploads in flight. */
  const [uploads, setUploads] = useState(0)

  /** Deletes a card, closing it if it was open. */
  function deleteCard(id: string) {
    const card = board.cards.find((n) => n.id === id)
    if (!card) return Promise.resolve(false)
    if (expanded?.id === id) setExpanded(null)
    setSelection(null)
    return settle(...ops.deleteCard(card, board))
  }

  function deleteImage(id: string) {
    const image = board.images.find((i) => i.id === id)
    if (!image) return
    unselect(id)
    void settle(...ops.deleteImage(image))
  }

  function deleteDrawing(id: string) {
    const drawing = board.drawings.find((d) => d.id === id)
    if (!drawing) return
    unselect(id)
    void settle(...ops.deleteDrawing(drawing))
  }

  /**
   * Uploads images one after another, cascading so they don't cover each other: onto the board around `at` (default:
   * the middle of the screen), or onto a card's canvas at `at` (default: a free spot). One image on the board is selected.
   */
  async function addImages(files: File[], place: { at?: Point; cardId?: string }) {
    if (files.length === 0) return
    const at = place.at ?? viewportCenter()
    const cascade = (p: Point, i: number) => snapPoint({ x: p.x + i * 40, y: p.y + i * 40 })
    setUploads((n) => n + 1)
    // One step for all of them; when one fails, for the ones uploaded before it.
    const label = (changes: unknown[]) => (changes.length === 1 ? 'Add image' : 'Add images')
    await runAll(runRec, label, undefined, async (changes) => {
      const added: string[] = []
      for (const [i, file] of files.entries()) {
        const placement = place.cardId
          ? { cardId: place.cardId, ...(place.at ? cascade(place.at, i) : {}) }
          : cascade({ x: at.x - IMAGE_OFFSET.x, y: at.y - IMAGE_OFFSET.y }, i)
        const image = await api.uploadImage(board.id, file, placement)
        added.push(image.id)
        changes.push(created(target.image(image.id), snap.image(image)))
      }
      if (added.length === 1 && !place.cardId) setSelection({ kind: 'image', id: added[0] })
    })
    setUploads((n) => n - 1)
  }

  return {
    tagSaving,
    uploads,
    deleteCard,
    deleteImage,
    deleteDrawing,
    addImages,
    /** Edits a card's fields. */
    editCard: (card: CardDetail, changes: CardChanges) => settle(...ops.editCard(card, changes)),
    /** Deletes one selected card, image or drawing (a bucket goes from its panel). */
    deleteOne(item: Selection) {
      if (item.kind === 'card') void deleteCard(item.id)
      else if (item.kind === 'image') deleteImage(item.id)
      else if (item.kind === 'drawing') deleteDrawing(item.id)
    },
    recolorDrawing(drawing: DrawingDetail, color: InkColor) {
      void settle(...ops.editDrawing(drawing, { color }, 'Change ink', ['colors']))
    },
    /** Folds a bucket up into a square in its top-right corner, hiding its cards, or opens it again with them where they were. */
    toggleCollapsed(bucket: BucketDetail) {
      const collapsed = !bucket.collapsed
      // What folds away with it is no longer selected. Opened, it comes to the front with its cards (the server puts it on top).
      if (collapsed) setSelected((list) => list.filter((s) => s.kind === 'bucket' || bucketOf(board, s) !== bucket.id))
      else toFront(bucket.id, false)
      const label = collapsed ? 'Collapse bucket' : 'Open bucket'
      void settle(
        () => api.updateBucket(bucket.id, { collapsed }),
        (after) => step(label, [patched(target.bucket(bucket.id), snap.bucket(bucket), snap.bucket(after), ['collapsed'])]),
      )
    },
    renameTag: (tag: BoardTag, name: string) => settle(...ops.renameTag(board.id, tag, name)),
    /** Recolors a tag; the new color shows right away and stays until the reloaded board has it. */
    async recolorTag(tag: BoardTag, color: TagColor) {
      setTagSaving({ tagId: tag.id, color })
      const ok = await settle(...ops.recolorTag(board.id, tag, color))
      setTagSaving((s) => (s?.tagId === tag.id && s.color === color ? null : s))
      return ok
    },
    deleteTag: (tag: BoardTag) => settle(...ops.deleteTag(board.id, tag)),
    async addImagesFromPicker(place: { cardId?: string } = {}) {
      await addImages(await pickImageFiles(), place)
    },
    /** Adds a bucket drawn with the bucket tool and selects it, its name ready to type. */
    addBucket(rect: Rect) {
      void settle(
        async () => {
          const bucket = await api.createBucket(board.id, { name: 'New bucket', ...rect })
          setSelection({ kind: 'bucket', id: bucket.id, fresh: true })
          setTool('select')
          return bucket
        },
        (bucket) => step('Add bucket', [created(target.bucket(bucket.id), snap.bucket(bucket))]),
      )
    },
    /** Adds a new card with its top-left corner at a (snapped) canvas point, selects it and opens it with its title ready to type. */
    stickCard(at: Point) {
      void settle(
        async () => {
          const card = await api.createCard(board.id, { title: '', color: fill, ...cardPlace(at) })
          setSelection({ kind: 'card', id: card.id })
          setExpanded({ id: card.id, fresh: true })
          setTool('select')
          return card
        },
        (card) => step('Add card', [created(target.card(card.id), snap.card(card))]),
      )
    },
  }
}
