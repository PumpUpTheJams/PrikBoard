import { useCallback, useRef, useState, type RefObject } from 'react'
import type { DropBucket, InkColor, LabelBlock, LabelChanges, LabelDetail, LabelSize, Point } from '../api/types'
import { LabelEditing } from './LabelEditing'
import { LabelView, type LabelBox } from './LabelView'
import { isBlankLabel, sameContent } from './labels'
import { measureLabel } from './measureLabel'
import { ops } from './ops'
import type { RecordingRun } from './undo'

/** What editing is about: a label there, or (`label` null) a new one, not saved until it has text. */
export interface LabelDraft {
  label: LabelDetail | null
  at: Point
  cardId: string | null
  ink: InkColor
  size: LabelSize
  wrapWidth: number | null
  minHeight: number | null
  background: boolean
  content: LabelBlock[]
  /** The bucket a new label on the board joins, by where it was clicked. */
  drop?: DropBucket
}

interface LabelEditingOptions {
  boardId: string
  /** Saves a change as an undo step, then lets go of the finished gesture. */
  settle: RecordingRun
  canvasRef: RefObject<HTMLElement | null>
}

/** What a label's box changes, for a PATCH: 0 lets go of a width or height. Its place only when it moved, since that can change its bucket. */
function boxChanges(label: LabelDetail, box: Pick<LabelDraft, 'at' | 'wrapWidth' | 'minHeight'>): LabelChanges {
  return {
    ...(box.at.x !== label.x || box.at.y !== label.y ? { x: box.at.x, y: box.at.y } : {}),
    ...(box.wrapWidth !== label.wrapWidth ? { wrapWidth: box.wrapWidth ?? 0 } : {}),
    ...(box.minHeight !== label.minHeight ? { minHeight: box.minHeight ?? 0 } : {}),
  }
}

const draftOf = (label: LabelDetail): LabelDraft => ({
  label,
  at: label,
  cardId: label.cardId,
  ink: label.ink,
  size: label.size,
  wrapWidth: label.wrapWidth,
  minHeight: label.minHeight,
  background: label.background,
  content: label.content,
})

/** Editing labels on a canvas (the board's or a card's): the draft, the save when done, and what a selected label's toolbar does. */
export function useLabelEditing({ boardId, settle, canvasRef }: LabelEditingOptions) {
  const [draft, setDraft] = useState<LabelDraft | null>(null)
  /** What was just saved, shown until the reload brings it, so the label doesn't blink back to what it said. */
  const [saving, setSaving] = useState<LabelDraft | null>(null)
  /** Ends the edit in progress, from outside the editor (a press elsewhere). */
  const commit = useRef<(() => void) | null>(null)

  const measure = (content: LabelBlock[], size: LabelSize, box: Pick<LabelDetail, 'wrapWidth' | 'minHeight'>) => measureLabel(canvasRef.current!, content, size, box)

  function finish(from: LabelDraft, content: LabelBlock[], ink: InkColor, size: LabelSize, background: boolean) {
    const blank = isBlankLabel(content)
    // Measured first: it renders at once, and the editor is still there until the draft goes.
    const box = blank ? null : measure(content, size, from)
    setDraft(null)
    if (from.label) {
      if (blank) {
        void settle(...ops.deleteLabel(from.label))
        return
      }
      const unchanged = ink === from.label.ink && size === from.label.size && background === from.label.background && sameContent(content, from.label.content)
      if (unchanged && !Object.keys(boxChanges(from.label, from)).length) return
    } else if (blank) {
      return
    }
    const shown = { ...from, content, ink, size, background }
    setSaving(shown)
    const done = () => setSaving((s) => (s === shown ? null : s))
    const saved = from.label
      ? settle(...ops.editLabel(from.label, { content, ink, size, background, ...boxChanges(from.label, from), ...box! }, 'Edit label', ['content', 'ink', 'size', 'background', ...Object.keys(boxChanges(from.label, from)), 'width', 'height']))
      : settle(...ops.addLabel(boardId, { content, ink, size, background, ...from.at, ...box!, ...(from.wrapWidth ? { wrapWidth: from.wrapWidth } : {}), ...(from.minHeight ? { minHeight: from.minHeight } : {}), ...(from.cardId ? { cardId: from.cardId } : {}), ...from.drop }))
    void saved.finally(done)
  }

  return {
    draft,
    saving,
    /** Ends the edit in progress, if any. */
    commit: () => commit.current?.(),
    /** A new label at a canvas point, or in the box drawn with the label tool. */
    startNew: useCallback((at: Point, style: { ink: InkColor; size: LabelSize }, cardId: string | null, drop?: DropBucket, box?: { width: number; height?: number }) => {
      setDraft({ label: null, at, cardId, ...style, wrapWidth: box?.width ?? null, minHeight: box?.height ?? null, background: cardId !== null, content: [], drop })
    }, []),
    edit: useCallback((label: LabelDetail) => {
      setDraft(draftOf(label))
    }, []),
    /** The editor and its toolbar, on the canvas. */
    editor: (zoom: number, onDelete: (label: LabelDetail) => void) =>
      draft && (
        <LabelEditing
          key={draft.label?.id ?? 'new'}
          draft={draft}
          zoom={zoom}
          commitRef={commit}
          onFinish={(content, ink, size, background) => finish(draft, content, ink, size, background)}
          onResize={({ x, y, width, minHeight }) => setDraft({ ...draft, at: { x, y }, wrapWidth: width, minHeight })}
          onDelete={() => {
            setDraft(null)
            if (draft.label) onDelete(draft.label)
          }}
        />
      ),
    /** What was just saved, on the canvas until the reload brings it. */
    savingView: () => saving && <LabelView label={{ id: saving.label?.id ?? 'new', ink: saving.ink, size: saving.size, content: saving.content, wrapWidth: saving.wrapWidth, minHeight: saving.minHeight, background: saving.background }} at={saving.at} />,
    /** A selected label in a new box. */
    resize: (label: LabelDetail, { x, y, width, minHeight }: LabelBox) => {
      const changes = boxChanges(label, { at: { x, y }, wrapWidth: width, minHeight })
      if (Object.keys(changes).length === 0) return
      const size = measure(label.content, label.size, { wrapWidth: width, minHeight })
      void settle(...ops.editLabel(label, { ...changes, ...size }, 'Resize label', [...Object.keys(changes), 'width', 'height']))
    },
    /** A selected label back to as wide and as tall as its text. */
    unbox: (label: LabelDetail) => {
      const size = measure(label.content, label.size, { wrapWidth: null, minHeight: null })
      void settle(...ops.editLabel(label, { wrapWidth: 0, minHeight: 0, ...size }, 'Fit label to its text', ['wrapWidth', 'minHeight', 'width', 'height']))
    },
    /** What the bar of a selected label (not being edited) does. Everything else is for editing. */
    selectedActions: (label: LabelDetail, onDelete: () => void) => ({
      background: {
        on: label.background,
        onToggle: () => void settle(...ops.editLabel(label, { background: !label.background }, 'Change background', ['background'])),
      },
      onEdit: () => setDraft(draftOf(label)),
      onDelete,
    }),
  }
}
