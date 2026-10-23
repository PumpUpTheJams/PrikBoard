import type { PointerEvent } from 'react'
import type { WindowEdge } from './useFloatingWindow'

const EDGES: WindowEdge[] = ['e', 's', 'se']

/** The right, bottom and corner edges a floating window is resized from (useFloatingWindow's `startResize`). */
export function WindowResizeHandles({ onResize }: { onResize: (edge: WindowEdge) => (e: PointerEvent) => void }) {
  return (
    <>
      {EDGES.map((edge) => (
        <span key={edge} className={`window-resize ${edge}`} aria-hidden="true" onPointerDown={onResize(edge)} />
      ))}
    </>
  )
}
