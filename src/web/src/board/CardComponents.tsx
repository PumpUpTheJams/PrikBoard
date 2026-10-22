import { lazy, Suspense, useEffect, useRef, useState, type FormEvent, type MouseEvent, type PointerEvent, type ReactNode } from 'react'
import type { ChecklistItem, CardComponent, CardDetail, CardLink, Rect, ResizeHandle } from '../api/types'
import { componentKinds } from './componentKinds'
import { EditableText } from './EditableText'
import { Highlight } from './Highlight'
import type { SearchTerm } from './search'
import { CloseIcon, LinkIcon, CardIcon, PencilIcon, PullRequestIcon, TrashIcon } from './icons'
import { isCardId, linkedCard, cardLabel, cardPath, cardRoute, type CardRoute } from './links'
import { ResizeHandles } from './ResizeHandles'

// Loaded on first use, so react-markdown stays out of the main bundle.
const MarkdownView = lazy(() => import('../components/MarkdownView'))

interface ComponentFrameProps {
  component: CardComponent
  /**
   * Where to draw it on the card's canvas: the server geometry, or the live geometry while moving or resizing. Without
   * one it is stacked in a list (the mobile layout), as wide as the list and as tall as its content.
   */
  rect?: Rect
  selected: boolean
  lifted: boolean
  /** The board search found its words in this component. */
  matched?: boolean
  readOnly?: boolean
  /** A press on the header (outside the title) starts moving the component. */
  onHeadPointerDown?: (e: PointerEvent<HTMLElement>) => void
  /**
   * A press on a corner or edge handle (shown while selected) starts resizing from there. Without it there are no
   * handles (one of several selected).
   */
  onResizePointerDown?: (e: PointerEvent<HTMLElement>, handle: ResizeHandle) => void
  /** A press in the component's content selects it (the header and handles select it as they start a gesture). */
  onSelect: () => void
  onTitle: (title: string) => void
  /** Asks to remove the component (the canvas confirms first). */
  onRemove: () => void
  /**
   * Opens the component in an editor of its own (the mobile layout edits there): an edit button at the end of the
   * header, shown even when the content itself is read only here.
   */
  onEdit?: () => void
  children: ReactNode
}

/** A component on a card's canvas: a header to drag it by, with its kind and an optional title, and its content. */
export function ComponentFrame(props: ComponentFrameProps) {
  const { component, rect, selected, lifted, matched, readOnly, onHeadPointerDown, onResizePointerDown, onSelect, onTitle, onRemove, onEdit, children } = props
  const { label, Icon } = componentKinds[component.kind]
  const classes = ['card-component', `kind-${component.kind.toLowerCase()}`]
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (matched) classes.push('matched')
  if (!rect) classes.push('stacked')

  return (
    <section
      className={classes.join(' ')}
      style={rect ? { left: rect.x, top: rect.y, width: rect.width, height: rect.height } : undefined}
      aria-label={component.title ?? label}
      data-component={component.id}
    >
      <header className="component-head" title={readOnly || !onHeadPointerDown ? undefined : 'Drag to move'} onPointerDown={onHeadPointerDown}>
        <Icon size={14} />
        <ComponentTitle value={component.title ?? ''} placeholder={label} readOnly={readOnly} onSave={onTitle} />
        {!readOnly && (
          <button type="button" className="component-remove" aria-label={`Remove ${component.title ?? label}`} title="Remove" onClick={onRemove}>
            <TrashIcon size={13} />
          </button>
        )}
        {onEdit && (
          <button type="button" className="component-edit" aria-label={`Edit ${component.title ?? label}`} title="Edit" onClick={onEdit}>
            <PencilIcon size={15} />
          </button>
        )}
      </header>
      <div className="component-body" onPointerDownCapture={onSelect}>
        {children}
      </div>
      {selected && !readOnly && onResizePointerDown && <ResizeHandles onPointerDown={onResizePointerDown} />}
    </section>
  )
}

/** The component's title: as wide as its text, so the rest of the header stays free to drag by. Saves on blur or Enter. */
function ComponentTitle({ value, placeholder, readOnly, onSave }: { value: string; placeholder: string; readOnly?: boolean; onSave: (value: string) => void }) {
  const [draft, setDraft] = useState<string | null>(null)
  const shown = draft ?? value
  return (
    <input
      className="component-title"
      aria-label="Component title"
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

interface TextComponentProps {
  text: string
  autoFocus: boolean
  readOnly?: boolean
  onChange: (text: string) => void
}

/**
 * A note, in Markdown: rendered while it isn't being edited, and a plain text area while it is. A click on the rendered
 * text edits it (unless it lands on a link or ends a text selection); an empty component is always the text area.
 */
export function TextComponent({ text, autoFocus, readOnly, onChange }: TextComponentProps) {
  const [editing, setEditing] = useState(false)
  const field = useRef<HTMLInputElement & HTMLTextAreaElement>(null)

  // Entering edit mode by clicking puts the text cursor at the end, rather than selecting everything like a new component.
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
        className="component-text"
        label="Note"
        value={text}
        maxLength={4000}
        multiline
        autoFocus={autoFocus && !editing}
        readOnly={readOnly}
        fieldRef={field}
        placeholder="Add details in Markdown: context, decisions, anything that doesn't fit on the card."
        onSave={onChange}
        onDone={() => setEditing(false)}
      />
    )
  }

  return (
    <div
      className="component-text component-view"
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
      <Suspense fallback={<div className="component-plain">{text}</div>}>
        <MarkdownView text={text} />
      </Suspense>
    </div>
  )
}

interface ChecklistComponentProps {
  items: ChecklistItem[]
  /** Search words to mark. */
  highlight?: SearchTerm[]
  autoFocus: boolean
  readOnly?: boolean
  /** Items can be ticked off, but not added or removed here (the mobile card; its editor does that). */
  tickOnly?: boolean
  onChange: (items: ChecklistItem[]) => Promise<boolean>
}

export function ChecklistComponent({ items, highlight, autoFocus, readOnly, tickOnly, onChange }: ChecklistComponentProps) {
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
        <span className="component-progress mono">
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
                <Highlight text={item.text} terms={highlight} field="component" />
              </span>
            </label>
            {!tickOnly && (
              <button
                type="button"
                className="row-remove"
                aria-label={`Remove "${item.text}"`}
                onClick={() => void onChange(items.filter((_, j) => j !== i))}
              >
                <CloseIcon size={12} />
              </button>
            )}
          </li>
        ))}
      </ul>
      {!tickOnly && (
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
      )}
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

interface LinksComponentProps {
  links: CardLink[]
  /** Search words to mark. */
  highlight?: SearchTerm[]
  /** The card the component is on, which is never offered as a link to itself. */
  card: CardDetail
  cards: CardDetail[]
  autoFocus: boolean
  /** Only the links, to open: no fields to add or remove them (the mobile card; its editor does that). */
  readOnly?: boolean
  onChange: (links: CardLink[]) => Promise<boolean>
}

/**
 * Links to web pages and to other cards. The address field takes a URL or a card's link or ID, or finds cards by
 * title or readable id (MPCT-415, or just 415).
 */
export function LinksComponent({ links, highlight, card, cards, autoFocus, readOnly, onChange }: LinksComponentProps) {
  const [url, setUrl] = useState('')
  const [title, setTitle] = useState('')
  const [invalid, setInvalid] = useState(false)
  const query = url.trim()
  // Typing words (not an address) looks for cards on this board to link.
  const searching = query.length >= 2 && !/^(https?:|\/#)/i.test(query)
  const linked = new Set(links.map((l) => linkedCard(cards, l.url)?.id))
  const lower = query.toLowerCase()
  const matches = searching
    ? cards
        .filter((n) => n.id !== card.id && !linked.has(n.id))
        .filter((n) => n.title.toLowerCase().includes(lower) || n.readableId.toLowerCase().includes(lower) || n.id.startsWith(lower))
        .slice(0, 5)
    : []

  async function addLink(link: CardLink) {
    if (await onChange([...links, link])) {
      setUrl('')
      setTitle('')
    }
  }

  /** Links a card by its path, titled by its current title unless the person typed one. */
  function addCard(target: CardRoute) {
    const found = linkedCard(cards, cardPath(target))
    return addLink({ title: title.trim() || (found ? cardLabel(found) : 'Card'), url: found?.path ?? cardPath(target) })
  }

  async function add(e: FormEvent) {
    e.preventDefault()
    const route = cardRoute(query) ?? (isCardId(query) ? { board: card.boardId, card: lower } : null)
    if (route) return addCard(route)
    if (matches.length > 0) return addCard({ board: matches[0].boardId, card: matches[0].id })
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
            <LinkRow link={link} cards={cards} highlight={highlight} />
            {!readOnly && (
              <button
                type="button"
                className="row-remove"
                aria-label={`Remove link ${link.title}`}
                onClick={() => void onChange(links.filter((_, j) => j !== i))}
              >
                <CloseIcon size={12} />
              </button>
            )}
          </li>
        ))}
      </ul>
      {!readOnly && (
        <form className="widget-add link-add" onSubmit={add}>
          <input
            aria-label="Link address, or a card to link"
            aria-invalid={invalid}
            placeholder="https://… or a card"
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
      )}
      {matches.length > 0 && (
        <ul className="links link-suggestions" aria-label="Cards to link">
          {matches.map((n) => (
            <li key={n.id}>
              <button type="button" className="link-row" onClick={() => void addCard({ board: n.boardId, card: n.id })}>
                <span className={`link-swatch fill-${n.color.toLowerCase()}`} aria-hidden="true" />
                <span className="link-text">
                  <span className="link-title">{cardLabel(n)}</span>
                  <span className="link-meta">{n.readableId}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {invalid && <p className="widget-error">Use a full http or https address, or a card's title, link or ID.</p>}
    </>
  )
}

/** One link: a web page opens in a new tab; a card opens here, shown with its current title and fill. */
function LinkRow({ link, cards, highlight }: { link: CardLink; cards: CardDetail[]; highlight?: SearchTerm[] }) {
  const route = cardRoute(link.url)
  if (route) {
    const target = linkedCard(cards, link.url)
    return (
      // Only the hash changes: the board opens the card, or App switches to its board.
      <a className="link-row" href={(target?.path ?? cardPath(route)).slice(1)}>
        {target ? <span className={`link-swatch fill-${target.color.toLowerCase()}`} aria-hidden="true" /> : <CardIcon size={16} />}
        <span className="link-text">
          <span className="link-title">
            <Highlight text={target ? cardLabel(target) : link.title} terms={highlight} field="component" />
          </span>
          <span className="link-meta">{target ? target.readableId : `On another board · ${route.board}/${route.card}`}</span>
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
          <Highlight text={link.title} terms={highlight} field="component" />
        </span>
        <span className="link-meta">
          <Highlight text={parsed ? `${parsed.host}${parsed.pathname}` : link.url} terms={highlight} field="component" />
        </span>
      </span>
    </a>
  )
}
