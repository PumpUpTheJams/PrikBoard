import { lazy, Suspense, useEffect, useRef, type RefObject } from 'react'
import type { InkColor, LabelBlock, LabelSize } from '../api/types'
import { inkColor } from './drawings'
import { LabelText } from './LabelText'
import { LabelToolbar } from './LabelView'
import { labelPixels } from './labels'
import type { LabelDraft } from './useLabelEditing'
import { useLabelTools } from './useLabelTools'

export const LabelEditor = lazy(() => import('./LabelEditor'))

interface LabelEditingProps {
  draft: LabelDraft
  zoom: number
  commitRef: RefObject<(() => void) | null>
  onFinish: (content: LabelBlock[], ink: InkColor, size: LabelSize, background: boolean) => void
  onDelete: () => void
}

/** A label being edited, in place on the canvas, with its toolbar above it. */
export function LabelEditing({ draft, zoom, commitRef, onFinish, onDelete }: LabelEditingProps) {
  const finished = useRef(false)
  const { handle, ink, size, background, onReady, onChange, tools } = useLabelTools({
    ink: draft.ink,
    size: draft.size,
    background: draft.background,
    onDelete: () => {
      finished.current = true
      onDelete()
    },
  })

  function finish() {
    if (finished.current) return
    finished.current = true
    onFinish(handle?.blocks() ?? draft.content, ink, size, background)
  }
  // The owner calls the latest one when something else is pressed.
  useEffect(() => {
    commitRef.current = finish
    return () => {
      commitRef.current = null
    }
  })

  return (
    <div
      className="label-editing"
      style={{ left: draft.at.x, top: draft.at.y }}
      // Presses here are for the text, not the canvas behind it.
      onPointerDown={(e) => e.stopPropagation()}
      onDoubleClick={(e) => e.stopPropagation()}
      onBlur={(e) => {
        // Not when the window loses focus, or for what is inside (the toolbar, the link's field).
        if (!document.hasFocus() || e.currentTarget.contains(e.relatedTarget)) return
        finish()
      }}
    >
      <div className={`label editing${draft.wrapWidth ? ' wraps' : ''}${background ? ' with-background' : ''}`} style={{ color: inkColor[ink], fontSize: labelPixels[size], width: draft.wrapWidth ?? undefined }}>
        <Suspense fallback={<LabelText content={draft.content} />}>
          <LabelEditor content={draft.content} onReady={onReady} onChange={onChange} onDone={finish} />
        </Suspense>
      </div>
      <LabelToolbar rect={{ x: 0, y: 0, width: 0, height: 0 }} zoom={zoom} {...tools} />
    </div>
  )
}
