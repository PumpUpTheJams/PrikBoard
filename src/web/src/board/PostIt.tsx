import { useLayoutEffect, useRef, useState, type CSSProperties, type MouseEvent, type PointerEvent, type ReactNode } from 'react'
import type { NoteDetail, Point } from '../api/types'
import { AvatarStack } from '../components/Avatar'
import { ExpandIcon } from './icons'
import { NOTE_HEIGHT, NOTE_WIDTH } from './layout'

/** Rough width of a tag chip: 11 px mono is about 6.6 px per character, plus padding and gap. */
const tagWidth = (tag: string) => tag.length * 6.6 + 16

/** Title lines that fit: three, or two when the tags wrap to a second row. */
function titleLines(note: NoteDetail) {
  const avatars = Math.min(note.assignees.length, 3)
  const room = NOTE_WIDTH - 24 - (avatars > 0 ? 24 + (avatars - 1) * 18 + 8 : 0)
  return note.tags.reduce((sum, tag) => sum + tagWidth(tag), 0) > room ? 2 : 3
}

interface PostItProps {
  note: NoteDetail
  /** Where to draw it: the server position, or the live position while dragging. */
  point: Point
  selected: boolean
  lifted: boolean
  /** Faded: it doesn't match the board's tag filter. */
  dimmed?: boolean
  /** An image being dragged would be attached to this post-it on release. */
  dropTarget?: boolean
  onPointerDown: (e: PointerEvent<HTMLDivElement>) => void
  onExpand: () => void
  /** For editors: open the tag or people window next to this part of the post-it (a screen rectangle). */
  onEditTags?: (anchor: DOMRect) => void
  onEditAssignees?: (anchor: DOMRect) => void
  /** The title is being edited in place (the board starts this on a click on the title). */
  editingTitle?: boolean
  /** Where the text cursor starts, as a character offset; default the end. */
  titleCaret?: number
  /** Saves a new title; resolves false when that failed. */
  onRenameTitle?: (title: string) => Promise<boolean>
  /** Editing the title ended (saved or cancelled). */
  onTitleDone?: () => void
}

/** On the board a post-it shows its title, tags and assignees. Everything else lives inside (see ExpandedNote). */
export function PostIt(props: PostItProps) {
  const { note, point, selected, lifted, dimmed, dropTarget, onPointerDown, onExpand, onEditTags, onEditAssignees } = props
  const { editingTitle, titleCaret, onRenameTitle, onTitleDone } = props
  /** A saved title, shown until the reload brings it (while the server still has `from`). */
  const [saved, setSaved] = useState<{ title: string; from: string } | null>(null)
  const title = saved && saved.from === note.title ? saved.title : note.title

  function rename(next: string) {
    onTitleDone?.()
    if (!onRenameTitle || next.length === 0 || next === note.title) return
    setSaved({ title: next, from: note.title })
    void onRenameTitle(next).then((ok) => ok || setSaved(null))
  }

  const classes = ['post-it', `fill-${note.color.toLowerCase()}`]
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (note.agentWorking) classes.push('agent-working')
  if (dimmed) classes.push('dimmed')
  if (dropTarget) classes.push('drop-target')

  const tags = note.tags.map((tag) => (
    <span key={tag} className="tag">
      {tag}
    </span>
  ))
  const people =
    note.assignees.length > 0 ? <AvatarStack assignees={note.assignees} /> : <span className="avatar-slot" aria-hidden="true" />

  return (
    <div
      className={classes.join(' ')}
      style={{ left: point.x, top: point.y, width: NOTE_WIDTH, height: NOTE_HEIGHT, '--title-lines': titleLines(note) } as CSSProperties}
      role="group"
      data-note={note.id}
      aria-label={note.agentWorking ? `${title} (agent working)` : title}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={onExpand}
      onKeyDown={(e) => e.key === 'Enter' && e.target === e.currentTarget && onExpand()}
    >
      <div className="post-it-head">
        {editingTitle && onRenameTitle ? (
          <TitleInput value={title} caret={titleCaret} onSave={rename} onCancel={() => onTitleDone?.()} />
        ) : (
          <span className={`post-it-title${onRenameTitle ? ' editable-title' : ''}`}>{title}</span>
        )}
        <button
          type="button"
          className="post-it-expand"
          aria-label={`Expand ${title}`}
          onPointerDown={(e) => e.stopPropagation()}
          onClick={onExpand}
        >
          <ExpandIcon />
        </button>
      </div>
      <div className="post-it-foot">
        {onEditTags ? (
          <QuickEdit className="post-it-tags" label={`Tags of ${title}`} onOpen={onEditTags}>
            {tags.length > 0 ? tags : <span className="tag tag-add">+ tag</span>}
          </QuickEdit>
        ) : (
          <span className="post-it-tags">{tags}</span>
        )}
        {onEditAssignees ? (
          <QuickEdit className="post-it-people" label={`People on ${title}`} onOpen={onEditAssignees}>
            {people}
          </QuickEdit>
        ) : (
          people
        )}
      </div>
    </div>
  )
}

/**
 * The title edited in place: Enter (or clicking elsewhere) saves, Escape cancels. Pointer and double-click events stay
 * inside, so selecting text doesn't drag the post-it and a double-click selects a word instead of expanding it.
 */
function TitleInput(props: { value: string; caret?: number; onSave: (title: string) => void; onCancel: () => void }) {
  const { value, caret, onSave, onCancel } = props
  const [draft, setDraft] = useState(value)
  const done = useRef(false)
  const input = useRef<HTMLTextAreaElement>(null)

  // Starts with the text cursor where the title was clicked; after that it follows the typing.
  const [start] = useState(() => Math.min(caret ?? value.length, value.length))
  useLayoutEffect(() => {
    input.current?.focus({ preventScroll: true })
    input.current?.setSelectionRange(start, start)
  }, [start])

  // Grows with the text, like the title in the expanded post-it.
  useLayoutEffect(() => {
    const el = input.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${el.scrollHeight}px`
  }, [draft])

  function finish(save: boolean) {
    if (done.current) return
    done.current = true
    if (save) onSave(draft.replace(/\s+/g, ' ').trim())
    else onCancel()
  }

  const stop = (e: PointerEvent | MouseEvent) => e.stopPropagation()
  return (
    <textarea
      ref={input}
      className="post-it-title-input"
      rows={1}
      aria-label="Title"
      value={draft}
      maxLength={200}
      onChange={(e) => setDraft(e.target.value)}
      onPointerDown={stop}
      onDoubleClick={stop}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault()
          finish(true)
        } else if (e.key === 'Escape') {
          // Only the edit, not the board's selection.
          e.stopPropagation()
          finish(false)
        }
      }}
      onBlur={() => finish(true)}
    />
  )
}

/** A part of the post-it that opens a window on click, without starting a drag or expanding the post-it. */
function QuickEdit({ className, label, onOpen, children }: { className: string; label: string; onOpen: (anchor: DOMRect) => void; children: ReactNode }) {
  const stop = (e: PointerEvent | MouseEvent) => e.stopPropagation()
  return (
    <button
      type="button"
      className={`${className} quick-edit`}
      aria-label={label}
      aria-haspopup="dialog"
      onPointerDown={stop}
      onDoubleClick={stop}
      onClick={(e) => onOpen(e.currentTarget.getBoundingClientRect())}
    >
      {children}
    </button>
  )
}
