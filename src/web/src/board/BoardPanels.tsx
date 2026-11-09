import type { BoardDetail, BoardTag, BucketChanges, BucketDetail, CardChanges, CardDetail, ImageChanges, ImageDetail, Rect, TagColor } from '../api/types'
import { AssigneePicker } from './AssigneePicker'
import { BucketInspector, ImageInspector } from './Inspector'
import type { TagColorPreview, TagColors } from './tagColors'
import { TagPicker } from './TagPicker'

/** The tag or people window opened from a card on the board, next to the part that was clicked (screen pixels). */
export interface QuickEdit {
  cardId: string
  kind: 'tags' | 'people'
  anchor: DOMRect
}

interface ItemPanelsProps {
  board: BoardDetail
  zoom: number
  /** The bucket selected by itself, where it is shown, with its panel; `fresh`: just added, its name ready to type. */
  bucket: { bucket: BucketDetail; rect: Rect } | undefined
  fresh: boolean
  onSaveBucket: (bucket: BucketDetail, changes: BucketChanges) => Promise<boolean>
  onDeleteBucket: (bucket: BucketDetail) => void
  /** The image selected by itself, where it is shown, with its panel. */
  image: { image: ImageDetail; rect: Rect } | undefined
  onSaveImage: (image: ImageDetail, changes: ImageChanges) => Promise<boolean>
  onDeleteImage: (image: ImageDetail) => void
  /** Closes the panel (letting go of the selection). */
  onClose: () => void
}

/** The selected bucket's or image's panel, on the canvas just above it. */
export function ItemPanels(props: ItemPanelsProps) {
  const { board, zoom, bucket, image } = props
  return (
    <>
      {bucket && (
        <BucketInspector
          // A new name (from an undo, or someone else) resets the field.
          key={`${bucket.bucket.id}:${bucket.bucket.name}`}
          rect={bucket.rect}
          zoom={zoom}
          bucket={bucket.bucket}
          cardCount={board.cards.filter((n) => n.bucketId === bucket.bucket.id).length}
          autoFocus={props.fresh}
          onSave={(changes) => props.onSaveBucket(bucket.bucket, changes)}
          onDelete={() => props.onDeleteBucket(bucket.bucket)}
          onClose={props.onClose}
        />
      )}

      {image && (
        <ImageInspector
          key={`${image.image.id}:${image.image.caption}`}
          rect={image.rect}
          zoom={zoom}
          image={image.image}
          onSave={(changes) => props.onSaveImage(image.image, changes)}
          onDelete={() => props.onDeleteImage(image.image)}
          onClose={props.onClose}
        />
      )}
    </>
  )
}

interface BoardPanelsProps {
  board: BoardDetail
  tagColors: TagColors
  quickEdit: QuickEdit | null
  onCloseQuickEdit: () => void
  editCard: (card: CardDetail, changes: CardChanges) => Promise<boolean>
  onRenameTag: (tag: BoardTag, name: string) => Promise<boolean>
  onRecolorTag: (tag: BoardTag, color: TagColor) => Promise<boolean>
  onPreviewTagColor: (preview: TagColorPreview | null) => void
  onDeleteTag: (tag: BoardTag) => Promise<boolean>
}

/** The tag or people window opened from a card on the board. */
export function BoardPanels(props: BoardPanelsProps) {
  const { board, tagColors, quickEdit, editCard } = props
  const quickCard = quickEdit && board.cards.find((n) => n.id === quickEdit.cardId)
  return (
    <>
      {quickCard && quickEdit.kind === 'tags' && (
        <TagPicker
          key={quickCard.id}
          anchor={quickEdit.anchor}
          tags={quickCard.tags}
          boardTags={board.tags}
          tagColors={tagColors}
          onChange={(tags) => editCard(quickCard, { tags })}
          onRename={props.onRenameTag}
          onRecolor={props.onRecolorTag}
          onPreview={props.onPreviewTagColor}
          onDelete={props.onDeleteTag}
          onClose={props.onCloseQuickEdit}
        />
      )}
      {quickCard && quickEdit.kind === 'people' && (
        <AssigneePicker
          key={quickCard.id}
          anchor={quickEdit.anchor}
          assignees={quickCard.assignees}
          people={board.people}
          onChange={(assigneeIds) => editCard(quickCard, { assigneeIds })}
          onClose={props.onCloseQuickEdit}
        />
      )}
    </>
  )
}
