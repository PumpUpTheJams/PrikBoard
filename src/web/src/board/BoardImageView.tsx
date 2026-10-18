import type { PointerEvent } from 'react'
import type { ImageDetail, Rect } from '../api/types'
import { initials } from '../components/initials'

interface BoardImageViewProps {
  image: ImageDetail
  /** Where to draw it: the server geometry, or the live geometry while moving or resizing. */
  rect: Rect
  selected: boolean
  lifted: boolean
  onPointerDown: (e: PointerEvent<HTMLElement>) => void
  onResizePointerDown: (e: PointerEvent<HTMLElement>) => void
}

/** An image or GIF on the canvas, with its file name (or caption) and who added it underneath. */
export function BoardImageView({ image, rect, selected, lifted, onPointerDown, onResizePointerDown }: BoardImageViewProps) {
  const classes = ['board-image']
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  const label = image.caption ?? image.fileName

  return (
    <figure
      className={classes.join(' ')}
      style={{ left: rect.x, top: rect.y, width: rect.width }}
      role="group"
      aria-label={label}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={() => window.open(image.contentUrl, '_blank', 'noopener')}
    >
      <div className="board-image-frame" style={{ height: rect.height }}>
        <img src={image.contentUrl} alt={label} draggable={false} />
        {image.contentType === 'image/gif' && <span className="gif-badge">GIF</span>}
        {selected && (
          <>
            <span className="handle nw" aria-hidden="true" />
            <span className="handle ne" aria-hidden="true" />
            <span className="handle sw" aria-hidden="true" />
            <button
              type="button"
              className="handle se resize-handle"
              aria-label={`Resize ${label}`}
              title="Drag to resize"
              onPointerDown={onResizePointerDown}
            />
          </>
        )}
      </div>
      <figcaption>
        <span className="board-image-name">{label}</span>
        <span title={`Added by ${image.addedBy}`}>added by {initials(image.addedBy)}</span>
      </figcaption>
    </figure>
  )
}
