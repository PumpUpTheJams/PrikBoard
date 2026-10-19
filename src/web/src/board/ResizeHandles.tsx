import type { PointerEvent } from 'react'
import type { ResizeHandle } from '../api/types'

const handles: { handle: ResizeHandle; className: string }[] = [
  { handle: 'Top', className: 'edge n' },
  { handle: 'Right', className: 'edge e' },
  { handle: 'Bottom', className: 'edge s' },
  { handle: 'Left', className: 'edge w' },
  { handle: 'TopLeft', className: 'handle nw' },
  { handle: 'TopRight', className: 'handle ne' },
  { handle: 'BottomLeft', className: 'handle sw' },
  { handle: 'BottomRight', className: 'handle se' },
]

/**
 * The resize controls of a selected item that can be resized: a square on each corner, which resizes both ways, and an
 * invisible strip along each edge, which resizes in that direction only. Items that can't be resized (post-its) don't
 * get them, only the selection outline.
 */
export function ResizeHandles({ onPointerDown }: { onPointerDown: (e: PointerEvent<HTMLElement>, handle: ResizeHandle) => void }) {
  return handles.map(({ handle, className }) => (
    <span key={handle} className={className} aria-hidden="true" onPointerDown={(e) => onPointerDown(e, handle)} />
  ))
}
