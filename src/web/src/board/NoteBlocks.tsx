import { lazy, Suspense, useEffect, useRef, useState, type FormEvent, type MouseEvent, type PointerEvent, type ReactNode } from 'react'
import type { ChecklistItem, NoteBlock, NoteDetail, NoteLink, Rect, ResizeHandle } from '../api/types'
import { blockKinds } from './blockKinds'
import { EditableText } from './EditableText'
import { Highlight } from './Highlight'
import { CloseIcon, LinkIcon, PostItIcon, PullRequestIcon, TrashIcon } from './icons'
import { isNoteId, linkedNote, noteLabel, notePath, noteRoute, type NoteRoute } from './links'
import { ResizeHandles } from './ResizeHandles'

// Loaded on first use, so react-markdown stays out of the main bundle.
const MarkdownView = lazy(() => import('../components/MarkdownView'))

interface BlockFrameProps {
  block: NoteBlock
  /** Where to draw it: the server geometry, or the live geometry while moving or resizing. */
  rect: Rect
  selected: boolean
  lifted: boolean
  /** The board search found its words in this block. */
  matched?: boolean
  readOnly?: boolean
  /** A press on the header (outside the title) starts moving the block. */
  onHeadPointerDown: (e: PointerEvent<HTMLElement>) => void
  /**
   * A press on a corner or edge handle (shown while selected) starts resizing from there. Without it there are no
   * handles (one of several selected).
   */
  onResizePointerDown?: (e: PointerEvent<HTMLElement>, handle: ResizeHandle) => void
  /** A press in the block's content selects it (the header and handles select it as they start a gesture). */
  onSelect: () => void
  onTitle: (title: string) => void
  /** Asks to remove the block (the canvas confirms first). */
  onRemove: () => void
  children: ReactNode
}

/** A block on a post-it's canvas: a header to drag it by, with its kind and an optional title, and its content. */
export function BlockFrame(props: BlockFrameProps) {
  const { block, rect, selected, lifted, matched, readOnly, onHeadPointerDown, onResizePointerDown, onSelect, onTitle, onRemove, children } = props
  const { label, Icon } = blockKinds[block.kind]
  const classes = ['note-block', `kind-${block.kind.toLowerCase()}`]
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (matched) classes.push('matched')

  return (
    <section
      className={classes.join(' ')}
      style={{ left: rect.x, top: rect.y, width: rect.width, height: rect.height }}
      aria-label={block.title ?? label}
      data-block={block.id}
    >
      <header className="block-head" title={readOnly ? undefined : 'Drag to move'} onPointerDown={onHeadPointerDown}>
        <Icon size={14} />
        <BlockTitle value={block.title ?? ''} placeholder={label} readOnly={readOnly} onSave={onTitle} />
        {!readOnly && (
          <button type="button" className="block-remove" aria-label={`Remove ${block.title ?? label}`} title="Remove" onClick={onRemove}>
            <TrashIcon size={13} />
          </button>
        )}
      </header>
      <div className="block-body" onPointerDownCapture={onSelect}>
        {children}
      </div>
      {selected && !readOnly && onResizePointerDown && <ResizeHandles onPointerDown={onResizePointerDown} />}
    </section>
  )
}

/** The block's title: as wide as its text, so the rest of the header stays free to drag by. Saves on blur or Enter. */
function BlockTitle({ value, placeholder, readOnly, onSave }: { value: string; placeholder: string; readOnly?: boolean; onSave: (value: string) => void }) {
  const [draft, setDraft] = useState<string | null>(null)
  const shown = draft ?? value
  return (
    <input
      className="block-title"
      aria-label="Block title"
      value={shown}
      placeholder={placeholder}
      maxLength={100}
      size={Math.max(4, Math.min(40, (shown || placeholder).length + 1))}
      readOnly={readOnly}
      onFocus={() => setDraft(value)}
      onChange={(e) => setDraft(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') e.currentTarget.blur()
        if (e.key === 'Escape') {
          e.stopPropagation()
          const input = e.currentTarget
          setDraft(value)
          requestAnimationFrame(() => input.blur())
        }
      }}
      onBlur={() => {
        if (draft !== null && draft.trim() !== value) onSave(draft.trim())
        setDraft(null)
      }}
    />
  )
}

interface TextBlockProps {
  text: string
  autoFocus: boolean
  readOnly?: boolean
  onChange: (text: string) => void
}

/**
 * Text in Markdown: rendered while it isn't being edited, and a plain text area while it is. A click on the rendered
 * text edits it (unless it lands on a link or ends a text selection); an empty block is always the text area.
 */
export function TextBlock({ text, autoFocus, readOnly, onChange }: TextBlockProps) {
  const [editing, setEditing] = useState(false)
  const field = useRef<HTMLInputElement & HTMLTextAreaElement>(null)

  // Entering edit mode by clicking puts the text cursor at the end, rather than selecting everything like a new block.
  useEffect(() => {
    const area = field.current
    if (!editing || !area) return
    area.focus({ preventScroll: true })
    area.setSelectionRange(area.value.length, area.value.length)
  }, [editing])

  function edit(e: MouseEvent<HTMLElement>) {
    if (readOnly || (e.target as HTMLElement).closest('a')) return
    if (!window.getSelection()?.isCollapsed) return
    setEditing(true)
  }

  if (editing || (!text.trim() && !readOnly)) {
    return (
      <EditableText
        className="block-text"
        label="Note"
        value={text}
        maxLength={4000}
        multiline
        autoFocus={autoFocus && !editing}
        readOnly={readOnly}
        fieldRef={field}
        placeholder="Add details in Markdown: context, decisions, anything that doesn't fit on the post-it."
        onSave={onChange}
        onDone={() => setEditing(false)}
      />
    )
  }

  return (
    <div
      className="block-text block-view"
      title={readOnly ? undefined : 'Click to edit'}
      tabIndex={readOnly ? undefined : 0}
      onClick={edit}
      onKeyDown={(e) => {
        if (!readOnly && e.target === e.currentTarget && e.key === 'Enter') {
          e.preventDefault()
          setEditing(true)
        }
      }}
    >
      <Suspense fallback={<div className="block-plain">{text}</div>}>
        <MarkdownView text={text} />
      </Suspense>
    </div>
  )
}

interface ChecklistBlockProps {
  items: ChecklistItem[]
  /** Search words to mark. */
  highlight?: string[]
  autoFocus: boolean
  readOnly?: boolean
  onChange: (items: ChecklistItem[]) => Promise<boolean>
}

export function ChecklistBlock({ items, highlight, autoFocus, readOnly, onChange }: ChecklistBlockProps) {
  const [text, setText] = useState('')
  const done = items.filter((i) => i.done).length

  async function add(e: FormEvent) {
    e.preventDefault()
    const value = text.trim()
    if (value && (await onChange([...items, { text: value, done: false }]))) setText('')
  }

  return (
    <>
      {items.length > 0 && (
        <span className="block-progress mono">
          {done} / {items.length}
        </span>
      )}
      <ul className="checklist">
        {items.map((item, i) => (
          <li key={i} className={item.done ? 'done' : undefined}>
            <label>
              <input
                type="checkbox"
                disabled={readOnly}
                checked={item.done}
                onChange={() => void onChange(items.map((it, j) => (j === i ? { ...it, done: !it.done } : it)))}
              />
              <span>
                <Highlight text={item.text} terms={highlight} />
              </span>
            </label>
            <button
              type="button"
              className="row-remove"
              aria-label={`Remove "${item.text}"`}
              onClick={() => void onChange(items.filter((_, j) => j !== i))}
            >
              <CloseIcon size={12} />
            </button>
          </li>
        ))}
      </ul>
      <form className="widget-add" onSubmit={add}>
        <input
          aria-label="New checklist item"
          placeholder="Add an item"
          value={text}
          maxLength={300}
          autoFocus={autoFocus}
          onChange={(e) => setText(e.target.value)}
        />
      </form>
    </>
  )
}

/** The address only when it is http(s), so a link can never run script. */
function webUrl(value: string): URL | null {
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url : null
  } catch {
    return null
  }
}

interface LinksBlockProps {
  links: NoteLink[]
  /** Search words to mark. */
  highlight?: string[]
  /** The post-it the block is on, which is never offered as a link to itself. */
  note: NoteDetail
  notes: NoteDetail[]
  autoFocus: boolean
  onChange: (links: NoteLink[]) => Promise<boolean>
}

/**
 * Links to web pages and to other post-its. The address field takes a URL or a post-it's link or ID, or finds post-its by
 * title or readable id (MPCT-415, or just 415).
 */
export function LinksBlock({ links, highlight, note, notes, autoFocus, onChange }: LinksBlockProps) {
  const [url, setUrl] = useState('')
  const [title, setTitle] = useState('')
  const [invalid, setInvalid] = useState(false)
  const query = url.trim()
  // Typing words (not an address) looks for post-its on this board to link.
  const searching = query.length >= 2 && !/^(https?:|\/#)/i.test(query)
  const linked = new Set(links.map((l) => linkedNote(notes, l.url)?.id))
  const lower = query.toLowerCase()
  const matches = searching
    ? notes
        .filter((n) => n.id !== note.id && !linked.has(n.id))
        .filter((n) => n.title.toLowerCase().includes(lower) || n.readableId.toLowerCase().includes(lower) || n.id.startsWith(lower))
        .slice(0, 5)
    : []

  async function addLink(link: NoteLink) {
    if (await onChange([...links, link])) {
      setUrl('')
      setTitle('')
    }
  }

  /** Links a post-it by its path, titled by its current title unless the person typed one. */
  function addNote(target: NoteRoute) {
    const found = linkedNote(notes, notePath(target))
    return addLink({ title: title.trim() || (found ? noteLabel(found) : 'Post-it'), url: found?.path ?? notePath(target) })
  }

  async function add(e: FormEvent) {
    e.preventDefault()
    const route = noteRoute(query) ?? (isNoteId(query) ? { board: note.boardId, note: lower } : null)
    if (route) return addNote(route)
    if (matches.length > 0) return addNote({ board: matches[0].boardId, note: matches[0].id })
    const parsed = webUrl(query)
    if (!parsed) {
      setInvalid(true)
      return
    }
    await addLink({ title: title.trim() || `${parsed.host}${parsed.pathname === '/' ? '' : parsed.pathname}`, url: parsed.href })
  }

  return (
    <>
      <ul className="links">
        {links.map((link, i) => (
          <li key={i}>
            <LinkRow link={link} notes={notes} highlight={highlight} />
            <button
              type="button"
              className="row-remove"
              aria-label={`Remove link ${link.title}`}
              onClick={() => void onChange(links.filter((_, j) => j !== i))}
            >
              <CloseIcon size={12} />
            </button>
          </li>
        ))}
      </ul>
      <form className="widget-add link-add" onSubmit={add}>
        <input
          aria-label="Link address, or a post-it to link"
          aria-invalid={invalid}
          placeholder="https://… or a post-it"
          value={url}
          maxLength={2000}
          autoFocus={autoFocus}
          onChange={(e) => {
            setUrl(e.target.value)
            setInvalid(false)
          }}
        />
        <input aria-label="Link title" placeholder="Title (optional)" value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
        <button type="submit" className="widget-button" disabled={!query}>
          Add
        </button>
      </form>
      {matches.length > 0 && (
        <ul className="links link-suggestions" aria-label="Post-its to link">
          {matches.map((n) => (
            <li key={n.id}>
              <button type="button" className="link-row" onClick={() => void addNote({ board: n.boardId, note: n.id })}>
                <span className={`link-swatch fill-${n.color.toLowerCase()}`} aria-hidden="true" />
                <span className="link-text">
                  <span className="link-title">{noteLabel(n)}</span>
                  <span className="link-meta">{n.readableId}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {invalid && <p className="widget-error">Use a full http or https address, or a post-it's title, link or ID.</p>}
    </>
  )
}

/** One link: a web page opens in a new tab; a post-it opens here, shown with its current title and fill. */
function LinkRow({ link, notes, highlight }: { link: NoteLink; notes: NoteDetail[]; highlight?: string[] }) {
  const route = noteRoute(link.url)
  if (route) {
    const target = linkedNote(notes, link.url)
    return (
      // Only the hash changes: the board opens the post-it, or App switches to its board.
      <a className="link-row" href={(target?.path ?? notePath(route)).slice(1)}>
        {target ? <span className={`link-swatch fill-${target.color.toLowerCase()}`} aria-hidden="true" /> : <PostItIcon size={16} />}
        <span className="link-text">
          <span className="link-title">
            <Highlight text={target ? noteLabel(target) : link.title} terms={highlight} />
          </span>
          <span className="link-meta">{target ? target.readableId : `On another board · ${route.board}/${route.note}`}</span>
        </span>
      </a>
    )
  }
  const parsed = webUrl(link.url)
  const Icon = /\/pulls?\//.test(link.url) ? PullRequestIcon : LinkIcon
  return (
    <a className="link-row" href={parsed?.href} target="_blank" rel="noreferrer noopener">
      <Icon />
      <span className="link-text">
        <span className="link-title">
          <Highlight text={link.title} terms={highlight} />
        </span>
        <span className="link-meta">
          <Highlight text={parsed ? `${parsed.host}${parsed.pathname}` : link.url} terms={highlight} />
        </span>
      </span>
    </a>
  )
}
