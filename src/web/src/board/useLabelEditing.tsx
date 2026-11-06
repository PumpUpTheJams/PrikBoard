import { useRef, useState, type RefObject } from 'react'
import type { DropBucket, InkColor, LabelBlock, LabelChanges, LabelDetail, LabelSize, Point } from '../api/types'
import { LabelEditing } from './LabelEditing'
import { HoveredLabelBar, LabelBar, LabelBoxHandles, LabelView, type LabelBox, type PlacedLabel } from './LabelView'
import { followLabelLink, isBlankLabel, NEW_LABEL_STYLE, sameContent } from './labels'
import { measureLabel } from './measureLabel'
import { ops } from './ops'
import type { LabelHover } from './useLabelHover'
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

/** How long a press on a link in a selected label waits to see it isn't a double-click, which edits instead. */
const LINK_DELAY_MS = 300

interface LabelChromeOptions {
  zoom: number
  /** The label selected, unless viewers are looking or it is being dragged. */
  selected: PlacedLabel | undefined
  /** Who the mouse is over; null while a mouse over a label shows nothing. */
  hover: LabelHover | null
  /** A label on the canvas by id; undefined for one the mouse can't bring a bar up on. */
  labelAt: (id: string) => PlacedLabel | undefined
  onDelete: (id: string) => void
}

/** Editing labels on a canvas (the board's or a card's): the draft, the save when done, and what a selected label's bar does. */
export function useLabelEditing({ boardId, settle, canvasRef }: LabelEditingOptions) {
  const [draft, setDraft] = useState<LabelDraft | null>(null)
  /** What was just saved, shown until the reload brings it, so the label doesn't blink back to what it said. */
  const [saving, setSaving] = useState<LabelDraft | null>(null)
  /** Ends the edit in progress, from outside the editor (a press elsewhere). */
  const commit = useRef<(() => void) | null>(null)
  /** The link pressed in a selected label, followed on release. */
  const link = useRef<string | null>(null)
  const linkTimer = useRef(0)

  const measure = (content: LabelBlock[], size: LabelSize, box: Pick<LabelDetail, 'wrapWidth' | 'minHeight'>) => measureLabel(canvasRef.current!, content, size, box)
  const edit = (label: LabelDetail) => setDraft(draftOf(label))

  function finish(from: LabelDraft, content: LabelBlock[], ink: InkColor, size: LabelSize, background: boolean) {
    const blank = isBlankLabel(content)
    const before = from.label
    const changes = before ? boxChanges(before, from) : {}
    const unchanged = !!before && ink === before.ink && size === before.size && background === before.background && sameContent(content, before.content) && !Object.keys(changes).length
    // Measured first: it renders at once, and the editor is still there until the draft goes.
    const box = blank || unchanged ? null : measure(content, size, from)
    setDraft(null)
    if (blank && before) void settle(...ops.deleteItem('label', before))
    if (blank || unchanged) return
    const shown = { ...from, content, ink, size, background }
    setSaving(shown)
    const saved = before
      ? settle(...ops.editLabel(before, { content, ink, size, background, ...changes, ...box! }, 'Edit label', ['content', 'ink', 'size', 'background', ...Object.keys(changes), 'width', 'height']))
      : settle(...ops.addLabel(boardId, { content, ink, size, background, ...from.at, ...box!, ...(from.wrapWidth ? { wrapWidth: from.wrapWidth } : {}), ...(from.minHeight ? { minHeight: from.minHeight } : {}), ...(from.cardId ? { cardId: from.cardId } : {}), ...from.drop }))
    void saved.finally(() => setSaving((s) => (s === shown ? null : s)))
  }

  /** A selected label in a new box. */
  function resize(label: LabelDetail, { x, y, width, minHeight }: LabelBox) {
    const changes = boxChanges(label, { at: { x, y }, wrapWidth: width, minHeight })
    if (Object.keys(changes).length === 0) return
    const size = measure(label.content, label.size, { wrapWidth: width, minHeight })
    void settle(...ops.editLabel(label, { ...changes, ...size }, 'Resize label', [...Object.keys(changes), 'width', 'height']))
  }

  /** A selected label back to as wide and as tall as its text. */
  function unbox(label: LabelDetail) {
    const size = measure(label.content, label.size, { wrapWidth: null, minHeight: null })
    void settle(...ops.editLabel(label, { wrapWidth: 0, minHeight: 0, ...size }, 'Fit label to its text', ['wrapWidth', 'minHeight', 'width', 'height']))
  }

  /** What the bar of a selected or hovered label does. Everything else is for editing. */
  const barOf = (label: LabelDetail, onDelete: (id: string) => void) => ({
    background: {
      on: label.background,
      onToggle: () => void settle(...ops.editLabel(label, { background: !label.background }, 'Change background', ['background'])),
    },
    onEdit: () => edit(label),
    onDelete: () => onDelete(label.id),
  })

  return {
    draft,
    /** The label being edited or just saved, which `editor` and `savingView` draw instead. */
    beingEdited: draft?.label?.id ?? saving?.label?.id,
    /** Ends the edit in progress, if any. */
    commit: () => commit.current?.(),
    /** A new label at a canvas point, in the box drawn with the label tool or as wide as one started with a click. */
    startNew(at: Point, cardId: string | null, box: { width: number; height?: number }, drop?: DropBucket) {
      setDraft({ label: null, at, cardId, ...NEW_LABEL_STYLE, wrapWidth: box.width, minHeight: box.height ?? null, background: cardId !== null, content: [], drop })
    },
    edit,
    /** A press on an item: remembers the link pressed in a selected label, if any. */
    pressLink(href: string | null) {
      window.clearTimeout(linkTimer.current)
      link.current = href
    },
    /** A click ended: follows that link, unless a double-click comes first. The tab it opens still counts as this click's. */
    followLink() {
      const href = link.current
      if (href) linkTimer.current = window.setTimeout(() => followLabelLink(href), LINK_DELAY_MS)
    },
    /** A double-click on a label: edits it, for those who may, instead of following its link. */
    doubleClick(label: LabelDetail, readOnly?: boolean) {
      window.clearTimeout(linkTimer.current)
      if (!readOnly) edit(label)
    },
    /** The editor and its toolbar, on the canvas. */
    editor: (zoom: number) =>
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
            if (draft.label) void settle(...ops.deleteItem('label', draft.label))
          }}
        />
      ),
    /** What was just saved, on the canvas until the reload brings it. */
    savingView: () => saving && <LabelView label={{ ...saving, id: saving.label?.id ?? 'new' }} at={saving.at} />,
    /** The bars and box handles of the selected and hovered labels, on the canvas, unless one is being edited. */
    chrome({ zoom, selected, hover, labelAt, onDelete }: LabelChromeOptions) {
      if (draft || saving) return null
      return (
        <>
          {selected && (
            <>
              <LabelBar rect={selected.rect} zoom={zoom} {...barOf(selected.label, onDelete)} />
              <LabelBoxHandles key={selected.label.id} id={selected.label.id} at={selected.rect} minHeight={selected.label.minHeight} zoom={zoom} onResize={(box) => resize(selected.label, box)} onReset={() => unbox(selected.label)} />
            </>
          )}
          {hover && <HoveredLabelBar hover={hover} labelAt={labelAt} zoom={zoom} barOf={(label) => barOf(label, onDelete)} />}
        </>
      )
    },
  }
}
