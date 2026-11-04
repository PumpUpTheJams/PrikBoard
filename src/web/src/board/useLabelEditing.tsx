import { useCallback, useRef, useState, type RefObject } from 'react'
import type { DropBucket, InkColor, LabelBlock, LabelDetail, LabelSize, Point } from '../api/types'
import { LabelEditing } from './LabelEditing'
import { LabelView } from './LabelView'
import { allHave, isBlankLabel, measureLabel, toggleAll, type LabelFormat, type LabelListKind } from './labels'
import { ops } from './ops'
import type { RecordingRun } from './undo'

/** What editing is about: a label there, or (`label` null) a new one, not saved until it has text. */
export interface LabelDraft {
  label: LabelDetail | null
  at: Point
  cardId: string | null
  ink: InkColor
  size: LabelSize
  content: LabelBlock[]
  /** The bucket a new label on the board joins, by where it was clicked. */
  drop?: DropBucket
  /** What the toolbar of a selected label asked for: done on all of its text once the editor is there. */
  then?: LabelListKind | 'link'
}

interface LabelEditingOptions {
  boardId: string
  /** Saves a change as an undo step, then lets go of the finished gesture. */
  settle: RecordingRun
  canvasRef: RefObject<HTMLElement | null>
}

/** Editing labels on a canvas (the board's or a card's): the draft, the save when done, and what a selected label's toolbar does. */
export function useLabelEditing({ boardId, settle, canvasRef }: LabelEditingOptions) {
  const [draft, setDraft] = useState<LabelDraft | null>(null)
  /** What was just saved, shown until the reload brings it, so the label doesn't blink back to what it said. */
  const [saving, setSaving] = useState<LabelDraft | null>(null)
  /** Ends the edit in progress, from outside the editor (a press elsewhere). */
  const commit = useRef<(() => void) | null>(null)

  const measure = (content: LabelBlock[], size: LabelSize) => measureLabel(canvasRef.current!, content, size)

  function finish(from: LabelDraft, content: LabelBlock[], ink: InkColor, size: LabelSize) {
    setDraft(null)
    const blank = isBlankLabel(content)
    if (from.label) {
      if (blank) {
        void settle(...ops.deleteLabel(from.label))
        return
      }
      const unchanged = ink === from.label.ink && size === from.label.size && JSON.stringify(content) === JSON.stringify(from.label.content)
      if (unchanged) return
    } else if (blank) {
      return
    }
    const box = measure(content, size)
    const shown = { ...from, content, ink, size }
    setSaving(shown)
    const done = () => setSaving((s) => (s === shown ? null : s))
    const saved = from.label
      ? settle(...ops.editLabel(from.label, { content, ink, size, ...box }, 'Edit label', ['content', 'ink', 'size', 'width', 'height']))
      : settle(...ops.addLabel(boardId, { content, ink, size, ...from.at, ...box, ...(from.cardId ? { cardId: from.cardId } : {}), ...from.drop }))
    void saved.finally(done)
  }

  return {
    draft,
    saving,
    /** Ends the edit in progress, if any. */
    commit: () => commit.current?.(),
    /** A new label at a canvas point. */
    startNew: useCallback((at: Point, style: { ink: InkColor; size: LabelSize }, cardId: string | null, drop?: DropBucket) => {
      setDraft({ label: null, at, cardId, ...style, content: [], drop })
    }, []),
    edit: useCallback((label: LabelDetail, then?: LabelDraft['then']) => {
      setDraft({ label, at: label, cardId: label.cardId, ink: label.ink, size: label.size, content: label.content, then })
    }, []),
    /** The editor and its toolbar, on the canvas. */
    editor: (zoom: number, onDelete: (label: LabelDetail) => void) =>
      draft && (
        <LabelEditing
          key={draft.label?.id ?? 'new'}
          draft={draft}
          zoom={zoom}
          commitRef={commit}
          onFinish={(content, ink, size) => finish(draft, content, ink, size)}
          onDelete={() => {
            setDraft(null)
            if (draft.label) onDelete(draft.label)
          }}
        />
      ),
    /** What was just saved, on the canvas until the reload brings it. */
    savingView: () => saving && <LabelView label={{ id: saving.label?.id ?? 'new', ink: saving.ink, size: saving.size, content: saving.content }} at={saving.at} />,
    /** What the toolbar of a selected label (not being edited) does to all of its text. Ink and size are only for editing. */
    selectedActions: (label: LabelDetail, onDelete: () => void) => {
      return {
        isActive: (format: LabelFormat) => allHave(label.content, format),
        onToggle: (format: LabelFormat) => {
          const content = toggleAll(label.content, format)
          void settle(...ops.editLabel(label, { content, ...measure(content, label.size) }, 'Change format', ['content', 'width', 'height']))
        },
        listActive: (kind: LabelListKind) => label.content.every((b) => b.kind === kind),
        // These need the text selected in the editor: they start it, and it does them.
        onToggleList: (kind: LabelListKind) => setDraft({ label, at: label, cardId: label.cardId, ink: label.ink, size: label.size, content: label.content, then: kind }),
        linkActive: label.content.some((b) => b.runs.some((r) => r.link)),
        onLink: () => setDraft({ label, at: label, cardId: label.cardId, ink: label.ink, size: label.size, content: label.content, then: 'link' }),
        onDelete,
      }
    },
  }
}
