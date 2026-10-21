import type { PointerEvent } from 'react'
import type { ImageDetail, Rect, ResizeHandle } from '../api/types'
import { initials } from '../components/initials'
import { keepPress } from './gesture'
import { TrashIcon } from './icons'
import { ResizeHandles } from './ResizeHandles'

interface BoardImageViewProps {
  image: ImageDetail
  /** Where to draw it: the server geometry, or the live geometry while moving or resizing. */
  rect: Rect
  selected: boolean
  lifted: boolean
  /** Drawn above the rest, with the bucket that was opened or pressed last. */
  front?: boolean
  onPointerDown: (e: PointerEvent<HTMLElement>) => void
  /** A press on a corner or edge handle (shown while selected, for editors) starts resizing from there. */
  onResizePointerDown?: (e: PointerEvent<HTMLElement>, handle: ResizeHandle) => void
  /** Asks to delete the image (the caller confirms first); without it there is no trash button (viewers). */
  onDelete?: () => void
}

/** An image or GIF on the canvas, with its file name (or caption) and who added it underneath. */
export function BoardImageView({ image, rect, selected, lifted, front, onPointerDown, onResizePointerDown, onDelete }: BoardImageViewProps) {
  const classes = ['board-image']
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (front) classes.push('in-front')
  const label = image.caption ?? image.fileName

  return (
    <figure
      className={classes.join(' ')}
      style={{ left: rect.x, top: rect.y, width: rect.width }}
      role="group"
      data-image={image.id}
      aria-label={label}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={() => window.open(image.contentUrl, '_blank', 'noopener')}
    >
      <div className="board-image-frame" style={{ height: rect.height }}>
        <img src={image.contentUrl} alt={label} draggable={false} />
        {image.contentType === 'image/gif' && <span className="gif-badge">GIF</span>}
        {onDelete && (
          <button
            type="button"
            className="image-delete"
            aria-label={`Delete ${label}`}
            title="Delete"
            // Not a drag: the canvas would otherwise take the pointer and the click with it.
            onPointerDown={keepPress}
            onClick={onDelete}
          >
            <TrashIcon size={13} />
          </button>
        )}
        {selected && onResizePointerDown && <ResizeHandles onPointerDown={onResizePointerDown} />}
      </div>
      <figcaption>
        <span className="board-image-name">{label}</span>
        <span title={`Added by ${image.addedBy}`}>added by {initials(image.addedBy)}</span>
      </figcaption>
    </figure>
  )
}
