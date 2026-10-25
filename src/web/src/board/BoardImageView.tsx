import type { PointerEvent } from 'react'
import type { ImageDetail, Rect, ResizeHandle } from '../api/types'
import { initials } from '../components/initials'
import { keepPress } from './gesture'
import { TrashIcon } from './icons'
import { ResizeHandles } from './ResizeHandles'

interface BoardImageViewProps {
  image: ImageDetail
  /**
   * Where to draw it on a canvas: the server geometry, or the live geometry while moving or resizing. Without one it is
   * part of a list (the mobile layout), as wide as the list at its own aspect ratio, and a tap opens it (`onOpen`).
   */
  rect?: Rect
  selected: boolean
  lifted: boolean
  /** Drawn above the rest, with the topmost open bucket it is in. */
  onPointerDown?: (e: PointerEvent<HTMLElement>) => void
  /** In a list: shows the image on its own. */
  onOpen?: () => void
  /** A press on a corner or edge handle (shown while selected, for editors) starts resizing from there. */
  onResizePointerDown?: (e: PointerEvent<HTMLElement>, handle: ResizeHandle) => void
  /** Deletes the image, without asking first (undo brings it back); without it there is no trash button (viewers). */
  onDelete?: () => void
}

/** An image or GIF on the canvas, with its file name (or caption) and who added it underneath. */
export function BoardImageView({ image, rect, selected, lifted, onPointerDown, onOpen, onResizePointerDown, onDelete }: BoardImageViewProps) {
  const classes = ['board-image']
  if (!rect) classes.push('in-list')
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  const label = image.caption ?? image.fileName

  return (
    <figure
      className={classes.join(' ')}
      style={rect ? { left: rect.x, top: rect.y, width: rect.width } : undefined}
      role="group"
      data-image={image.id}
      aria-label={label}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={rect ? () => window.open(image.contentUrl, '_blank', 'noopener') : undefined}
      onClick={rect ? undefined : onOpen}
      onKeyDown={(e) => !rect && e.key === 'Enter' && e.target === e.currentTarget && onOpen?.()}
    >
      <div className="board-image-frame" style={rect ? { height: rect.height } : { aspectRatio: `${image.pixelWidth} / ${image.pixelHeight}` }}>
        <img src={image.contentUrl} alt={label} draggable={false} loading="lazy" decoding="async" />
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
        {/* Unnamed on a public board that hides its people. */}
        {image.addedBy && <span title={`Added by ${image.addedBy}`}>added by {initials(image.addedBy)}</span>}
      </figcaption>
    </figure>
  )
}
