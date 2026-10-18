import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type RefObject } from 'react'
import {
  noteColors,
  type Assignee,
  type AssigneeKind,
  type BucketDetail,
  type ChecklistItem,
  type NoteChanges,
  type NoteColor,
  type NoteDetail,
  type NoteLink,
} from '../api/types'
import { Avatar } from '../components/Avatar'
import { ConfirmDeleteNote } from '../components/ConfirmDialog'
import {
  BucketIcon,
  ChecklistIcon,
  CloseIcon,
  LinkIcon,
  NoteDocIcon,
  PullRequestIcon,
  SparkleIcon,
  TrashIcon,
} from './icons'

// Limits mirror the API's validation (src/PrikBoard.Api/Contracts/Notes.cs).
const MAX_TAGS = 10
const MAX_ASSIGNEES = 10

interface ExpandedNoteProps {
  note: NoteDetail
  bucket: BucketDetail | undefined
  /** Focus and select the title, for a post-it that was just stuck on. */
  focusTitle: boolean
  onChange: (changes: NoteChanges) => Promise<boolean>
  /** Deletes the post-it; called after the person confirmed. */
  onDelete: () => void
  onClose: () => void
}

/** A post-it opened up: a header band in its fill, and a mini board with its note, checklist and links. */
export function ExpandedNote({ note, bucket, focusTitle, onChange, onDelete, onClose }: ExpandedNoteProps) {
  const [reveal, setReveal] = useState({ checklist: false, links: false })
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const dialogRef = useRef<HTMLElement>(null)
  const bodyRef = useRef<HTMLTextAreaElement>(null)
  const checklistRef = useRef<HTMLInputElement>(null)
  const linkRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (!focusTitle) dialogRef.current?.focus()
  }, [focusTitle])

  // Escape closes the post-it wherever focus is (it can end up on the page after saving the title). Fields and the
  // delete confirmation handle their own Escape first and stop it from getting here.
  useEffect(() => {
    if (confirmingDelete) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [confirmingDelete, onClose])

  function show(widget: 'checklist' | 'links') {
    setReveal((r) => ({ ...r, [widget]: true }))
    const ref = widget === 'checklist' ? checklistRef : linkRef
    requestAnimationFrame(() => ref.current?.focus())
  }

  return (
    <div className="expanded-backdrop" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <article
        ref={dialogRef}
        className={`expanded fill-${note.color.toLowerCase()}`}
        role="dialog"
        aria-modal="true"
        aria-label={note.title}
        tabIndex={-1}
      >
        <header className="band">
          <div className="band-row">
            <span className="band-bucket">
              <BucketIcon />
              {bucket ? bucket.name : 'Loose on the board'}
            </span>
            <div className="band-actions">
              <FillPicker value={note.color} onChange={(color) => void onChange({ color })} />
              {note.agentWorking && (
                <button
                  type="button"
                  className="agent-chip"
                  title="The agent has finished: clear the halo"
                  onClick={() => void onChange({ agentWorking: false })}
                >
                  <SparkleIcon />
                  Agent working
                  <CloseIcon size={12} />
                </button>
              )}
              <button type="button" className="band-button icon" aria-label="Close" title="Close" onClick={onClose}>
                <CloseIcon />
              </button>
            </div>
          </div>
          <EditableText
            className="band-title"
            label="Title"
            value={note.title}
            maxLength={200}
            autoFocus={focusTitle}
            onSave={(title) => title.length > 0 && void onChange({ title })}
          />
          <div className="band-row">
            <TagEditor tags={note.tags} onChange={(tags) => onChange({ tags })} />
            <AssigneeEditor assignees={note.assignees} onChange={(assignees) => onChange({ assignees })} />
          </div>
        </header>

        <div className="mini-board">
          <div className="widgets">
            <section className="widget">
              <h2>Note</h2>
              <EditableText
                className="widget-body"
                label="Note"
                value={note.body ?? ''}
                maxLength={4000}
                multiline
                placeholder="Add details: context, decisions, anything that doesn't fit on the post-it."
                inputRef={bodyRef}
                onSave={(body) => void onChange({ body })}
              />
            </section>
            {(note.checklist.length > 0 || reveal.checklist) && (
              <ChecklistWidget items={note.checklist} inputRef={checklistRef} onChange={(checklist) => onChange({ checklist })} />
            )}
            {(note.links.length > 0 || reveal.links) && (
              <LinksWidget links={note.links} inputRef={linkRef} onChange={(links) => onChange({ links })} />
            )}
          </div>

          <div className="dock" role="toolbar" aria-label="Post-it tools">
            <button type="button" className="dock-button" onClick={() => bodyRef.current?.focus()}>
              <NoteDocIcon />
              Note
            </button>
            <button type="button" className="dock-button" onClick={() => show('checklist')}>
              <ChecklistIcon />
              Checklist
            </button>
            <button type="button" className="dock-button" onClick={() => show('links')}>
              <LinkIcon />
              Link
            </button>
            <span className="dock-divider" aria-hidden="true" />
            <button
              type="button"
              className="dock-button dock-delete"
              aria-label="Delete post-it"
              title="Delete post-it"
              onClick={() => setConfirmingDelete(true)}
            >
              <TrashIcon />
            </button>
          </div>
        </div>
      </article>

      {confirmingDelete && (
        <ConfirmDeleteNote
          title={note.title}
          onConfirm={() => {
            setConfirmingDelete(false)
            onDelete()
          }}
          onCancel={() => {
            setConfirmingDelete(false)
            dialogRef.current?.focus()
          }}
        />
      )}
    </div>
  )
}

interface EditableTextProps {
  value: string
  onSave: (value: string) => void
  label: string
  className: string
  maxLength: number
  multiline?: boolean
  placeholder?: string
  autoFocus?: boolean
  inputRef?: RefObject<HTMLTextAreaElement | null>
}

/**
 * A growing text area that saves on blur (and on Enter unless multiline). Escape cancels.
 * While it isn't focused it follows the server's value, so edits made by agents show up.
 */
function EditableText({ value, onSave, label, className, maxLength, multiline, placeholder, autoFocus, inputRef }: EditableTextProps) {
  const [draft, setDraft] = useState<string | null>(null)
  const cancelled = useRef(false)
  const local = useRef<HTMLTextAreaElement | null>(null)
  const shown = draft ?? value

  // Grow with the text.
  useLayoutEffect(() => {
    const el = local.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${el.scrollHeight}px`
  }, [shown])

  return (
    <textarea
      ref={(el) => {
        local.current = el
        if (inputRef) inputRef.current = el
      }}
      className={`editable ${className}`}
      aria-label={label}
      rows={1}
      value={shown}
      maxLength={maxLength}
      placeholder={placeholder}
      autoFocus={autoFocus}
      onFocus={(e) => {
        setDraft(value)
        if (autoFocus) e.target.select()
      }}
      onChange={(e) => setDraft(multiline ? e.target.value : e.target.value.replace(/\n/g, ' '))}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.stopPropagation()
          cancelled.current = true
          e.currentTarget.blur()
        } else if (e.key === 'Enter' && !multiline) {
          e.preventDefault()
          e.currentTarget.blur()
        }
      }}
      onBlur={() => {
        const next = (draft ?? value).trim()
        if (!cancelled.current && next !== value.trim()) onSave(next)
        cancelled.current = false
        setDraft(null)
      }}
    />
  )
}

interface InlineAddProps {
  placeholder: string
  label: string
  className?: string
  onAdd: (value: string) => void
  onDone: () => void
}

/** A small input that adds an item on Enter and stays open for the next one; Escape or leaving it empty closes it. */
function InlineAdd({ placeholder, label, className, onAdd, onDone }: InlineAddProps) {
  const [text, setText] = useState('')

  function add() {
    const value = text.trim()
    if (value) onAdd(value)
    setText('')
    return value
  }

  return (
    <input
      autoFocus
      className={`inline-add ${className ?? ''}`}
      aria-label={label}
      placeholder={placeholder}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault()
          add()
        } else if (e.key === 'Escape') {
          e.stopPropagation()
          onDone()
        }
      }}
      onBlur={() => {
        add()
        onDone()
      }}
    />
  )
}

function FillPicker({ value, onChange }: { value: NoteColor; onChange: (color: NoteColor) => void }) {
  return (
    <div className="band-fills" role="group" aria-label="Fill">
      {noteColors.map((c) => (
        <button
          key={c}
          type="button"
          className={`swatch fill-${c.toLowerCase()}`}
          title={c}
          aria-label={c}
          aria-pressed={c === value}
          onClick={() => onChange(c)}
        />
      ))}
    </div>
  )
}

function TagEditor({ tags, onChange }: { tags: string[]; onChange: (tags: string[]) => Promise<boolean> }) {
  const [adding, setAdding] = useState(false)

  return (
    <div className="band-tags">
      {tags.map((tag) => (
        <span key={tag} className="band-tag">
          {tag}
          <button type="button" className="chip-remove" aria-label={`Remove tag ${tag}`} onClick={() => void onChange(tags.filter((t) => t !== tag))}>
            <CloseIcon size={10} />
          </button>
        </span>
      ))}
      {adding ? (
        <InlineAdd
          className="mono"
          label="New tag"
          placeholder="tag"
          onAdd={(tag) => void onChange([...tags, tag])}
          onDone={() => setAdding(false)}
        />
      ) : (
        tags.length < MAX_TAGS && (
          <button type="button" className="band-add" onClick={() => setAdding(true)}>
            + tag
          </button>
        )
      )}
    </div>
  )
}

function AssigneeEditor({ assignees, onChange }: { assignees: Assignee[]; onChange: (assignees: Assignee[]) => Promise<boolean> }) {
  const [adding, setAdding] = useState(false)
  const [kind, setKind] = useState<AssigneeKind>('Person')

  return (
    <div className="band-assignees">
      {assignees.map((a) => (
        <span key={`${a.kind}:${a.name}`} className="band-assignee">
          <Avatar assignee={a} />
          {a.name}
          <button
            type="button"
            className="chip-remove"
            aria-label={`Unassign ${a.name}`}
            onClick={() => void onChange(assignees.filter((x) => x !== a))}
          >
            <CloseIcon size={10} />
          </button>
        </span>
      ))}
      {adding ? (
        <span className="assign-form">
          <span className="segmented" role="group" aria-label="Assignee kind">
            {(['Person', 'Agent'] as const).map((k) => (
              <button
                key={k}
                type="button"
                aria-pressed={kind === k}
                // Keep focus in the name field, which closes the form when it loses focus.
                onPointerDown={(e) => e.preventDefault()}
                onClick={() => setKind(k)}
              >
                {k}
              </button>
            ))}
          </span>
          <InlineAdd
            label={kind === 'Agent' ? 'Agent name' : 'Name'}
            placeholder={kind === 'Agent' ? 'Agent name' : 'Name'}
            onAdd={(name) => void onChange([...assignees, { name, kind }])}
            onDone={() => setAdding(false)}
          />
        </span>
      ) : (
        assignees.length < MAX_ASSIGNEES && (
          <button type="button" className="band-add" onClick={() => setAdding(true)}>
            + assign
          </button>
        )
      )}
    </div>
  )
}

interface ChecklistWidgetProps {
  items: ChecklistItem[]
  inputRef: RefObject<HTMLInputElement | null>
  onChange: (items: ChecklistItem[]) => Promise<boolean>
}

function ChecklistWidget({ items, inputRef, onChange }: ChecklistWidgetProps) {
  const [text, setText] = useState('')
  const done = items.filter((i) => i.done).length

  async function add(e: FormEvent) {
    e.preventDefault()
    const value = text.trim()
    if (value && (await onChange([...items, { text: value, done: false }]))) setText('')
  }

  return (
    <section className="widget">
      <div className="widget-head">
        <h2>Checklist</h2>
        {items.length > 0 && (
          <span className="mono">
            {done} / {items.length}
          </span>
        )}
      </div>
      <ul className="checklist">
        {items.map((item, i) => (
          <li key={i} className={item.done ? 'done' : undefined}>
            <label>
              <input
                type="checkbox"
                checked={item.done}
                onChange={() => void onChange(items.map((it, j) => (j === i ? { ...it, done: !it.done } : it)))}
              />
              <span>{item.text}</span>
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
        <input ref={inputRef} aria-label="New checklist item" placeholder="Add an item" value={text} maxLength={300} onChange={(e) => setText(e.target.value)} />
      </form>
    </section>
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

interface LinksWidgetProps {
  links: NoteLink[]
  inputRef: RefObject<HTMLInputElement | null>
  onChange: (links: NoteLink[]) => Promise<boolean>
}

function LinksWidget({ links, inputRef, onChange }: LinksWidgetProps) {
  const [url, setUrl] = useState('')
  const [title, setTitle] = useState('')
  const [invalid, setInvalid] = useState(false)

  async function add(e: FormEvent) {
    e.preventDefault()
    const parsed = webUrl(url.trim())
    if (!parsed) {
      setInvalid(true)
      return
    }
    const link = { title: title.trim() || `${parsed.host}${parsed.pathname === '/' ? '' : parsed.pathname}`, url: parsed.href }
    if (await onChange([...links, link])) {
      setUrl('')
      setTitle('')
    }
  }

  return (
    <section className="widget">
      <h2>Links</h2>
      <ul className="links">
        {links.map((link, i) => {
          const parsed = webUrl(link.url)
          const Icon = /\/pulls?\//.test(link.url) ? PullRequestIcon : LinkIcon
          return (
            <li key={i}>
              <a className="link-row" href={parsed?.href} target="_blank" rel="noreferrer noopener">
                <Icon />
                <span className="link-text">
                  <span className="link-title">{link.title}</span>
                  <span className="link-meta">{parsed ? `${parsed.host}${parsed.pathname}` : link.url}</span>
                </span>
              </a>
              <button
                type="button"
                className="row-remove"
                aria-label={`Remove link ${link.title}`}
                onClick={() => void onChange(links.filter((_, j) => j !== i))}
              >
                <CloseIcon size={12} />
              </button>
            </li>
          )
        })}
      </ul>
      <form className="widget-add link-add" onSubmit={add}>
        <input
          ref={inputRef}
          aria-label="Link address"
          aria-invalid={invalid}
          placeholder="https://…"
          value={url}
          maxLength={2000}
          onChange={(e) => {
            setUrl(e.target.value)
            setInvalid(false)
          }}
        />
        <input aria-label="Link title" placeholder="Title (optional)" value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} />
        <button type="submit" className="widget-button" disabled={!url.trim()}>
          Add
        </button>
      </form>
      {invalid && <p className="widget-error">Use a full http or https address.</p>}
    </section>
  )
}
