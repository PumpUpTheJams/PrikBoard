import type { ResizeHandle } from '../api/types'

/** The resize cursor for each handle, also shown on the canvas while it holds the pointer during the resize. */
export const resizeCursor: Record<ResizeHandle, string> = {
  Top: 'ns-resize',
  Bottom: 'ns-resize',
  Left: 'ew-resize',
  Right: 'ew-resize',
  TopLeft: 'nwse-resize',
  BottomRight: 'nwse-resize',
  TopRight: 'nesw-resize',
  BottomLeft: 'nesw-resize',
}
