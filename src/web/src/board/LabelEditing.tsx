import { lazy, Suspense, useEffect, useRef, type RefObject } from 'react'
import { inkColor } from './drawings'
import { LabelText } from './LabelText'
import { LabelBoxHandles, LabelHead, LabelToolbar, type LabelBox } from './LabelView'
import { labelClasses, labelPixels } from './labels'
import type { LabelDraft, LabelEdited } from './useLabelEditing'
import { useLabelTools } from './useLabelTools'

export const LabelEditor = lazy(() => import('./LabelEditor'))

interface LabelEditingProps {
  draft: LabelDraft
  zoom: number
  commitRef: RefObject<(() => void) | null>
  onFinish: (edited: LabelEdited) => void
  onResize: (box: LabelBox) => void
  onDelete: () => void
}

/** A label being edited, in place on the canvas, with its toolbar above it. */
export function LabelEditing({ draft, zoom, commitRef, onFinish, onResize, onDelete }: LabelEditingProps) {
  const finished = useRef(false)
  const { handle, ink, size, background, title, onReady, onChange, tools } = useLabelTools({
    ink: draft.ink,
    size: draft.size,
    background: draft.background,
    title: draft.title,
    onDelete: () => {
      finished.current = true
      onDelete()
    },
    onDone: () => finish(),
  })

  function finish() {
    if (finished.current) return
    finished.current = true
    onFinish({ content: handle?.blocks() ?? draft.content, ink, size, background, title })
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
        // Not when the window loses focus, or for what is inside (the toolbar, its fields).
        if (!document.hasFocus() || e.currentTarget.contains(e.relatedTarget)) return
        finish()
      }}
    >
      <div
        className={`label editing${labelClasses({ wrapWidth: draft.wrapWidth, background, title: title.trim() })}`}
        style={{ color: inkColor[ink], fontSize: labelPixels[size], width: draft.wrapWidth ?? undefined, minHeight: draft.minHeight ?? undefined }}
      >
        {title.trim() && <LabelHead title={title.trim()} />}
        <Suspense fallback={<LabelText content={draft.content} />}>
          <LabelEditor content={draft.content} caret={draft.caret} onReady={onReady} onChange={onChange} onDone={finish} />
        </Suspense>
      </div>
      <LabelBoxHandles at={draft.at} minHeight={draft.minHeight} zoom={zoom} onResize={onResize} />
      <LabelToolbar zoom={zoom} {...tools} />
    </div>
  )
}
