import type { BoardDetail, ImageDetail } from '../api/types'
import { DownloadIcon, TrashIcon } from '../board/icons'
import { bucketPath } from '../board/links'
import { ops } from '../board/ops'
import type { RecordingRun } from '../board/undo'
import { back } from './nav'
import { TopBar } from './parts'

/** An image on its own, fitted to the screen (the phone's own zoom enlarges it), opened from a bucket or a card. */
export function ImageScreen({ board, image, run }: { board: BoardDetail; image: ImageDetail; run: RecordingRun }) {
  const card = image.cardId ? board.cards.find((c) => c.id === image.cardId) : undefined
  const bucket = board.buckets.find((b) => b.id === image.bucketId)
  // Up is where the image is: its card, or its bucket on the board.
  const up = card ? card.path : bucketPath(board.shortId, image.bucketId)
  const label = image.caption ?? image.fileName

  async function remove() {
    if (await run(...ops.deleteImage(image))) back(up)
  }

  return (
    <div className="m-screen m-image-screen">
      <TopBar up={{ to: up, label: 'Back', text: card ? card.readableId : (bucket?.name ?? 'Back') }} context={`Added by ${image.addedBy}`} title={label} />

      <div className="m-image-view">
        <img src={image.contentUrl} alt={label} />
      </div>

      <div className="m-actions">
        <a className="m-action" href={image.contentUrl} target="_blank" rel="noreferrer noopener" download={image.fileName}>
          <DownloadIcon />
          Open original
        </a>
        {board.yourRole !== 'Viewer' && (
          <button type="button" className="m-action danger" onClick={() => void remove()}>
            <TrashIcon />
            Delete
          </button>
        )}
      </div>
    </div>
  )
}
