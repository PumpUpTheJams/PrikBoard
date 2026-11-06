import { Suspense, useState } from 'react'
import type { CardDetail, InkColor, LabelBlock, LabelDetail, LabelSize } from '../api/types'
import { inkColor } from '../board/drawings'
import { keepTextSelected } from '../board/gesture'
import { LabelEditor } from '../board/LabelEditing'
import { labelPixels, NEW_LABEL_STYLE } from '../board/labels'
import { LabelText } from '../board/LabelText'
import { LabelTools } from '../board/LabelView'
import { useLabelTools } from '../board/useLabelTools'

interface LabelScreenProps {
  card: CardDetail
  /** The label being edited; without one, a new one is written. */
  label?: LabelDetail
  onCancel: () => void
  /** Saves it; the screen closes when that worked. */
  onSave: (content: LabelBlock[], ink: InkColor, size: LabelSize, background: boolean) => Promise<void>
  onRemove: () => void
}

/** A label written on a screen of its own, new or opened with its edit button: a draft until Add or Save, like `ComponentEditor`. */
export function LabelScreen({ card, label, onCancel, onSave, onRemove }: LabelScreenProps) {
  const content = label?.content ?? []
  const [busy, setBusy] = useState(false)
  const { handle, ink, size, background, onReady, onChange, tools } = useLabelTools({ ink: label?.ink ?? NEW_LABEL_STYLE.ink, size: label?.size ?? NEW_LABEL_STYLE.size, background: label?.background ?? true, onDelete: onRemove })

  function save() {
    setBusy(true)
    void onSave(handle?.blocks() ?? content, ink, size, background).finally(() => setBusy(false))
  }

  return (
    <div className="m-screen m-editor">
      <header className="m-bar">
        <button type="button" className="m-bar-text" onClick={onCancel}>
          Cancel
        </button>
        <div className="m-bar-title centered">
          <span className="m-bar-name">{label ? 'Edit' : 'New'} label</span>
          <span className="m-bar-context mono">{card.readableId}</span>
        </div>
        <button type="button" className="m-bar-primary" disabled={busy} onClick={save}>
          {label ? 'Save' : 'Add'}
        </button>
      </header>
      <div className="m-label-tools" role="toolbar" aria-label="Label" onMouseDown={keepTextSelected}>
        <LabelTools {...tools} />
      </div>
      <div className="m-scroll m-stack">
        <div className="m-label-editor">
          <div className={`label editing wraps${background ? ' with-background' : ''}`} style={{ color: inkColor[ink], fontSize: labelPixels[size] }}>
            <Suspense fallback={<LabelText content={content} />}>
              <LabelEditor content={content} onReady={onReady} onChange={onChange} onDone={onCancel} />
            </Suspense>
          </div>
        </div>
      </div>
    </div>
  )
}
