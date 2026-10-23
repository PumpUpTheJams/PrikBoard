import { useLayoutEffect, useRef, useState, type CSSProperties, type MouseEvent, type PointerEvent, type ReactNode } from 'react'
import type { CardDetail, Point } from '../api/types'
import { AvatarStack } from '../components/Avatar'
import { plural } from '../components/plural'
import { Highlight } from './Highlight'
import type { SearchTerm } from './search'
import { CheckIcon, ExpandIcon, LinkIcon, PutBackIcon, TrashIcon } from './icons'
import { cardLabel } from './links'
import { CARD_HEIGHT, CARD_WIDTH } from './layout'
import { keepPress } from './gesture'
import { tagColorClass, type TagColors } from './tagColors'
import { useCopyLink } from './useCopy'

/** What the card holds inside, by kind, e.g. "2 notes, 1 checklist, 1 image". */
function contentSummary(card: CardDetail, images: number) {
  const count = (kind: string) => card.components.filter((b) => b.kind === kind).length
  const parts = [
    [count('Text'), 'note', 'notes'],
    [count('Checklist'), 'checklist', 'checklists'],
    [count('Links'), 'link list', 'link lists'],
    [images, 'image', 'images'],
  ] as const
  return parts
    .filter(([n]) => n > 0)
    .map(([n, one, many]) => plural(n, one, many))
    .join(', ')
}

/** Keeps a press or double-click on a part of the card to that part, so it doesn't drag or expand the card. */
const stop = (e: PointerEvent | MouseEvent) => e.stopPropagation()

/** Rough width of a tag chip: 11 px mono is about 6.6 px per character, plus padding and gap. */
const tagWidth = (tag: string) => tag.length * 6.6 + 16

/** Title lines that fit: three, or two when the tags wrap to a second row. */
function titleLines(card: CardDetail) {
  const avatars = Math.min(card.assignees.length, 3)
  const room = CARD_WIDTH - 24 - (avatars > 0 ? 24 + (avatars - 1) * 18 + 8 : 0)
  return card.tags.reduce((sum, tag) => sum + tagWidth(tag), 0) > room ? 2 : 3
}

interface CardProps {
  card: CardDetail
  /**
   * Where to draw it on the board: the server position, or the live position while dragging. Without one it is part
   * of a list (the mobile layout), as wide as the list, and a tap opens it.
   */
  point?: Point
  /** How many images are attached to it (counted with its components in the corner). */
  images?: number
  /** The board's tag colors; tags without one are a shade of the fill. */
  tagColors?: TagColors
  selected: boolean
  lifted: boolean
  /** Drawn above the rest, with the topmost open bucket it is in. */
  front?: boolean
  /** Faded: it doesn't match the board's filters or search. */
  dimmed?: boolean
  /** Search words to mark in the title and tags. */
  highlight?: SearchTerm[]
  /** The search result being looked at. */
  current?: boolean
  /** An image being dragged would be attached to this card on release. */
  dropTarget?: boolean
  onPointerDown?: (e: PointerEvent<HTMLDivElement>) => void
  onExpand: () => void
  /** For editors: open the tag or people window next to this part of the card (a screen rectangle). */
  onEditTags?: (anchor: DOMRect) => void
  onEditAssignees?: (anchor: DOMRect) => void
  /** The title is being edited in place (the board starts this on a click on the title). */
  editingTitle?: boolean
  /** Where the text cursor starts, as a character offset; default the end. */
  titleCaret?: number
  /** Saves a new title; resolves false when that failed. */
  onRenameTitle?: (title: string) => Promise<boolean>
  /** Editing the title ended (saved or canceled). */
  onTitleDone?: () => void
  /**
   * For a card in the trash: puts it back where it was on the board. Its button takes the place of the people button
   * (the people still show, but can't be changed there).
   */
  onPutBack?: () => void
  /** For a card in the trash: deletes it for good. Its button sits next to put back. */
  onPurge?: () => void
}

/**
 * On the board a card shows its title, tags, assignees and how many things it holds. Everything else lives inside
 * (see ExpandedCard). The count sits in the top-right corner when there is anything inside; on hover it becomes the
 * expand button, with copy link (its tooltip names the readable id) below it.
 */
export function Card(props: CardProps) {
  const { card, point, images = 0, tagColors, selected, lifted, front, dimmed, highlight, current, dropTarget, onPointerDown, onExpand, onEditTags, onEditAssignees } = props
  const { editingTitle, titleCaret, onRenameTitle, onTitleDone, onPutBack, onPurge } = props
  /** A title just saved, shown until the reload brings it (while the card still has the old title, `from`). */
  const [saved, setSaved] = useState<{ title: string; from: string } | null>(null)
  const title = saved && saved.from === card.title ? saved.title : card.title
  /** The title in labels, with a stand-in when it is empty. */
  const name = cardLabel({ title })
  const { copied, copy } = useCopyLink(card)

  function rename(next: string) {
    onTitleDone?.()
    if (!onRenameTitle || next === card.title) return
    setSaved({ title: next, from: card.title })
    void onRenameTitle(next).then((ok) => ok || setSaved(null))
  }

  const classes = ['card', `fill-${card.color.toLowerCase()}`]
  if (!point) classes.push('in-list')
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (front) classes.push('in-front')
  if (card.agentWorking) classes.push('agent-working')
  if (dimmed) classes.push('dimmed')
  if (current) classes.push('search-current')
  if (dropTarget) classes.push('drop-target')
  if (copied !== null) classes.push('link-copied')

  const tags = card.tags.map((tag) => (
    <span key={tag} className={`tag${tagColorClass(tagColors, tag)}`}>
      <Highlight text={tag} terms={highlight} field="tag" />
    </span>
  ))
  const people =
    card.assignees.length > 0 ? <AvatarStack assignees={card.assignees} /> : <span className="avatar-slot" aria-hidden="true" />
  const contents = card.components.length + images
  const summary = contentSummary(card, images)
  // The corner's buttons act on their own click: pressing one doesn't start a drag (keepPress still lets the middle
  // button pan), and double-clicking one doesn't also expand the card.

  return (
    <div
      className={classes.join(' ')}
      style={point ? ({ left: point.x, top: point.y, width: CARD_WIDTH, height: CARD_HEIGHT, '--title-lines': titleLines(card) } as CSSProperties) : undefined}
      role="group"
      data-card={card.id}
      aria-label={card.agentWorking ? `${name} (agent working)` : name}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={point ? onExpand : undefined}
      onClick={point ? undefined : onExpand}
      onKeyDown={(e) => e.key === 'Enter' && e.target === e.currentTarget && onExpand()}
    >
      <div className="card-head">
        {editingTitle && onRenameTitle ? (
          <TitleInput value={title} caret={titleCaret} onSave={rename} onCancel={() => onTitleDone?.()} />
        ) : (
          <span className={`card-title${onRenameTitle ? ' editable-title' : ''}`}>
            <Highlight text={title} terms={highlight} field="title" />
          </span>
        )}
      </div>
      <div className="card-corner">
        {contents > 0 && (
          <span className="card-count" role="img" title={summary} aria-label={summary}>
            {contents}
          </span>
        )}
        <button type="button" className="expand" aria-label={`Expand ${name}`} title="Expand" onPointerDown={keepPress} onClick={onExpand}>
          <ExpandIcon size={13} />
        </button>
        <button
          type="button"
          className={copied ? 'copied' : undefined}
          aria-label={`Copy a link to ${name} (${card.readableId})`}
          title={`Copy link to ${card.readableId}`}
          onPointerDown={keepPress}
          onDoubleClick={stop}
          onClick={copy}
        >
          {copied ? <CheckIcon size={13} /> : <LinkIcon size={13} />}
        </button>
      </div>
      {copied !== null && (
        <span className="card-copied" role="status">
          {copied ? 'Link copied' : "Couldn't copy the link"}
        </span>
      )}
      <div className="card-foot">
        {onEditTags ? (
          <QuickEdit className="card-tags" label={`Tags of ${name}`} onOpen={onEditTags}>
            {tags.length > 0 ? tags : <span className="tag tag-add">+ tag</span>}
          </QuickEdit>
        ) : (
          <span className="card-tags">{tags}</span>
        )}
        {onPutBack ? (
          <span className="card-people put-back-slot">
            {card.assignees.length > 0 && <AvatarStack assignees={card.assignees} />}
            {onPurge && (
              <button
                type="button"
                className="card-put-back purge"
                aria-label={`Delete ${name} for good`}
                title="Delete for good"
                onPointerDown={stop}
                onDoubleClick={stop}
                onClick={onPurge}
              >
                <TrashIcon size={14} />
              </button>
            )}
            <button
              type="button"
              className="card-put-back"
              aria-label={`Put ${name} back on the board`}
              title="Put back on the board"
              onPointerDown={stop}
              onDoubleClick={stop}
              onClick={onPutBack}
            >
              <PutBackIcon />
            </button>
          </span>
        ) : onEditAssignees ? (
          <QuickEdit className="card-people" label={`People on ${name}`} onOpen={onEditAssignees}>
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
 * The title edited in place: Enter (or clicking elsewhere) saves, Escape cancels. Presses (except the middle button's, which
 * pans) and double-clicks stay inside, so selecting text doesn't drag the card and a double-click selects a word
 * instead of expanding it.
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

  // Grows with the text, like the title in the expanded card.
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

  return (
    <textarea
      ref={input}
      className="card-title-input"
      rows={1}
      aria-label="Title"
      value={draft}
      maxLength={200}
      onChange={(e) => setDraft(e.target.value)}
      onPointerDown={keepPress}
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

/** A part of the card that opens a window on click, without starting a drag or expanding the card. */
function QuickEdit({ className, label, onOpen, children }: { className: string; label: string; onOpen: (anchor: DOMRect) => void; children: ReactNode }) {
  return (
    <button
      type="button"
      className={`${className} quick-edit`}
      aria-label={label}
      aria-haspopup="dialog"
      onPointerDown={keepPress}
      onDoubleClick={stop}
      onClick={(e) => onOpen(e.currentTarget.getBoundingClientRect())}
    >
      {children}
    </button>
  )
}
