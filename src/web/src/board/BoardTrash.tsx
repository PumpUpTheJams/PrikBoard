import type { RefObject } from 'react'
import type { BoardDetail, Point } from '../api/types'
import { ExpandedCard, type ExpandedCardHandle } from './ExpandedCard'
import type { TagColors } from './tagColors'
import { TrashWindow } from './TrashWindow'
import type { RecordingRun } from './undo'
import type { TrashWindowState } from './useTrashWindow'

interface BoardTrashProps {
  board: BoardDetail
  trash: TrashWindowState
  readOnly: boolean
  tagColors: TagColors
  zoom: number
  /** A card from the board is being dragged over the window, to be deleted. */
  dropOver: boolean
  /** Whether a card is open: the trash's card then doesn't show (one card window at a time). */
  cardOpen: boolean
  /** Closes the open card, to show one from the trash instead. */
  onCloseCard: () => void
  run: RecordingRun
  onCardBusy: (busy: boolean) => void
  cardHandle: RefObject<ExpandedCardHandle | null>
  boardPoint: (client: Point) => Point | null
}

/** The trash window, and a card in the trash shown as it is inside: read only, with its images. */
export function BoardTrash({ board, trash, readOnly, tagColors, zoom, dropOver, cardOpen, onCloseCard, run, onCardBusy, cardHandle, boardPoint }: BoardTrashProps) {
  const preview = trash.preview
  return (
    <>
      {trash.open && (
        <TrashWindow
          boardId={board.id}
          reload={board}
          readOnly={readOnly}
          tagColors={tagColors}
          handle={trash.ref}
          dropOver={dropOver}
          boardZoom={zoom}
          onClose={trash.close}
          onOpenCard={(card, images) => {
            onCloseCard()
            trash.setPreview({ card, images })
          }}
          onPutBack={(card) => trash.putBack(card)}
          onPurge={trash.purge}
          onEmpty={trash.empty}
          onOutgoing={trash.setOutgoing}
          onDropOut={trash.dropOut}
          onBusy={trash.setBusy}
        />
      )}

      {preview && !cardOpen && (
        <ExpandedCard
          key={`trash:${preview.card.id}`}
          card={preview.card}
          bucket={undefined}
          images={preview.images}
          cards={board.cards}
          handle={cardHandle}
          run={run}
          onBusy={onCardBusy}
          incoming={null}
          linking={false}
          boardPoint={boardPoint}
          boardZoom={zoom}
          focusTitle={false}
          onChange={async () => false}
          boardTags={board.tags}
          tagColors={tagColors}
          people={board.people}
          onRenameTag={async () => false}
          onRecolorTag={async () => false}
          onPreviewTagColor={() => {}}
          onDeleteTag={async () => false}
          onDelete={() => {}}
          onAddImages={() => {}}
          onPickImages={() => {}}
          onClose={() => trash.setPreview(null)}
          readOnly
          deleted={{ onPutBack: readOnly ? undefined : () => trash.putBack(preview.card) }}
        />
      )}
    </>
  )
}
