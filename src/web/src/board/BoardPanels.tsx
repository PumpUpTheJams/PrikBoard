import type { BoardDetail, BoardTag, BucketChanges, BucketDetail, CardChanges, CardDetail, ImageChanges, ImageDetail, TagColor } from '../api/types'
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

interface BoardPanelsProps {
  board: BoardDetail
  tagColors: TagColors
  /** The bucket selected by itself, with its panel; `fresh`: just added, its name ready to type. */
  bucket: BucketDetail | undefined
  fresh: boolean
  onSaveBucket: (bucket: BucketDetail, changes: BucketChanges) => Promise<boolean>
  onDeleteBucket: (bucket: BucketDetail) => void
  /** The image selected by itself, with its panel. */
  image: ImageDetail | undefined
  onSaveImage: (image: ImageDetail, changes: ImageChanges) => Promise<boolean>
  onDeleteImage: (image: ImageDetail) => void
  /** Closes the panel (letting go of the selection). */
  onClose: () => void
  quickEdit: QuickEdit | null
  onCloseQuickEdit: () => void
  editCard: (card: CardDetail, changes: CardChanges) => Promise<boolean>
  onRenameTag: (tag: BoardTag, name: string) => Promise<boolean>
  onRecolorTag: (tag: BoardTag, color: TagColor) => Promise<boolean>
  onPreviewTagColor: (preview: TagColorPreview | null) => void
  onDeleteTag: (tag: BoardTag) => Promise<boolean>
}

/** The selected bucket's or image's panel, and the tag or people window opened from a card on the board. */
export function BoardPanels(props: BoardPanelsProps) {
  const { board, tagColors, bucket, image, quickEdit, editCard } = props
  const quickCard = quickEdit && board.cards.find((n) => n.id === quickEdit.cardId)
  return (
    <>
      {bucket && (
        <BucketInspector
          // A new name (from an undo, or someone else) resets the field.
          key={`${bucket.id}:${bucket.name}`}
          bucket={bucket}
          cardCount={board.cards.filter((n) => n.bucketId === bucket.id).length}
          autoFocus={props.fresh}
          onSave={(changes) => props.onSaveBucket(bucket, changes)}
          onDelete={() => props.onDeleteBucket(bucket)}
          onClose={props.onClose}
        />
      )}

      {image && (
        <ImageInspector
          key={`${image.id}:${image.caption}`}
          image={image}
          onSave={(changes) => props.onSaveImage(image, changes)}
          onDelete={() => props.onDeleteImage(image)}
          onClose={props.onClose}
        />
      )}

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
