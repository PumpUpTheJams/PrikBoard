import { lazy, Suspense, useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import type { InkColor, LabelBlock, LabelSize } from '../api/types'
import { inkColor } from './drawings'
import { LabelText } from './LabelText'
import { LabelToolbar } from './LabelView'
import { labelPixels, type LabelEditorHandle } from './labels'
import type { LabelDraft } from './useLabelEditing'

const LabelEditor = lazy(() => import('./LabelEditor'))

interface LabelEditingProps {
  draft: LabelDraft
  zoom: number
  commitRef: RefObject<(() => void) | null>
  onFinish: (content: LabelBlock[], ink: InkColor, size: LabelSize) => void
  onDelete: () => void
}

/** A label being edited, in place on the canvas, with its toolbar above it. */
export function LabelEditing({ draft, zoom, commitRef, onFinish, onDelete }: LabelEditingProps) {
  const [handle, setHandle] = useState<LabelEditorHandle | null>(null)
  const [ink, setInk] = useState(draft.ink)
  const [size, setSize] = useState(draft.size)
  /** The selection changed: what the toolbar shows follows it. */
  const [, setTick] = useState(0)
  const [link, setLink] = useState<{ value: string; error: boolean; existing: boolean } | null>(null)
  const finished = useRef(false)

  function finish() {
    if (finished.current) return
    finished.current = true
    onFinish(handle?.blocks() ?? draft.content, ink, size)
  }
  // The owner calls the latest one when something else is pressed.
  useEffect(() => {
    commitRef.current = finish
    return () => {
      commitRef.current = null
    }
  })

  const openLink = (from: LabelEditorHandle | null) => {
    const href = from?.linkHref() ?? null
    setLink({ value: href ?? '', error: false, existing: !!href })
  }

  // Once the editor is there, what the selected label's toolbar asked for.
  const then = draft.then
  const onReady = useCallback((ready: LabelEditorHandle) => {
    setHandle(ready)
    if (!then) return
    ready.selectAll()
    if (then === 'link') openLink(ready)
    else ready.toggleList(then)
  }, [then])

  const onChange = useCallback(() => setTick((t) => t + 1), [])

  function applyLink() {
    if (!link) return
    const url = link.value.trim()
    if (!handle?.setLink(url)) {
      setLink({ ...link, error: true })
      return
    }
    setLink(null)
  }

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
      <div className="label editing" style={{ color: inkColor[ink], fontSize: labelPixels[size] }}>
        <Suspense fallback={<LabelText content={draft.content} />}>
          <LabelEditor content={draft.content} onReady={onReady} onChange={onChange} onDone={finish} />
        </Suspense>
      </div>
      <LabelToolbar
        rect={{ x: 0, y: 0, width: 0, height: 0 }}
        zoom={zoom}
        ink={ink}
        size={size}
        isActive={(format) => !!handle?.isActive(format)}
        onToggle={(format) => handle?.toggle(format)}
        listActive={(kind) => !!handle?.listActive(kind)}
        onToggleList={(kind) => handle?.toggleList(kind)}
        linkActive={!!handle?.linkHref()}
        onLink={() => (link ? setLink(null) : openLink(handle))}
        // With text selected, for that text; else for the label.
        onInk={(value) => (handle?.hasSelection() ? handle.setInk(value) : setInk(value))}
        onSize={(value) => (handle?.hasSelection() ? handle.setSize(value) : setSize(value))}
        onDelete={() => {
          finished.current = true
          onDelete()
        }}
        link={
          link && {
            ...link,
            onChange: (value) => setLink({ ...link, value, error: false }),
            onApply: applyLink,
            onRemove: () => {
              handle?.unsetLink()
              setLink(null)
            },
            onCancel: () => {
              setLink(null)
              handle?.focus()
            },
          }
        }
      />
    </div>
  )
}
