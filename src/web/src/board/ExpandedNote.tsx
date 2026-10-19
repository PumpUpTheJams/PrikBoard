import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type RefObject } from 'react'
import {
  noteColors,
  type Assignee,
  type BoardPerson,
  type BoardTag,
  type BucketDetail,
  type ChecklistItem,
  type ImageDetail,
  type NoteChanges,
  type NoteColor,
  type NoteDetail,
  type NoteLink,
} from '../api/types'
import { Avatar } from '../components/Avatar'
import { ConfirmDeleteNote, ConfirmDialog } from '../components/ConfirmDialog'
import { AssigneePicker } from './AssigneePicker'
import { draggingFiles, imageFiles } from './files'
import {
  BucketIcon,
  CheckIcon,
  ChecklistIcon,
  CloseIcon,
  ImageIcon,
  LinkIcon,
  NoteDocIcon,
  PostItIcon,
  PullRequestIcon,
  SparkleIcon,
  TrashIcon,
} from './icons'
import { copyText, isNoteId, notePath, noteRoute, type NoteRoute } from './links'
import { TagPicker } from './TagPicker'
import { MAX_ASSIGNEES, MAX_TAGS } from './tags'

interface ExpandedNoteProps {
  note: NoteDetail
  bucket: BucketDetail | undefined
  /** Focus and select the title, for a post-it that was just stuck on. */
  focusTitle: boolean
  onChange: (changes: NoteChanges) => Promise<boolean>
  /** The board's tags and the people who can be assigned, to pick from. */
  boardTags: BoardTag[]
  people: BoardPerson[]
  onRenameTag: (tag: BoardTag, name: string) => Promise<boolean>
  onDeleteTag: (tag: BoardTag) => Promise<boolean>
  /** Deletes the post-it; called after the person confirmed. */
  onDelete: () => void
  /** Images attached to this post-it. */
  images: ImageDetail[]
  /** The post-its on the board, to link to and to show linked ones by their current title. */
  notes: NoteDetail[]
  onAddImages: (files: File[]) => void
  onPickImages: () => void
  /** Deletes an attached image; called after the person confirmed. */
  onDeleteImage: (imageId: string) => void
  onClose: () => void
  /** Viewers see the post-it without any way to change it (the API refuses changes anyway). */
  readOnly?: boolean
}

/** A post-it opened up: a header band in its fill, and a mini board with its note, checklist and links. */
export function ExpandedNote(props: ExpandedNoteProps) {
  const { note, bucket, images, focusTitle, onChange, onDelete, onAddImages, onPickImages, onDeleteImage, onClose, readOnly } = props
  const { boardTags, people, notes, onRenameTag, onDeleteTag } = props
  const [reveal, setReveal] = useState({ checklist: false, links: false })
  /** The tag or people window, opened from its button in the band. */
  const [picking, setPicking] = useState<{ kind: 'tags' | 'people'; anchor: DOMRect } | null>(null)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [imageToDelete, setImageToDelete] = useState<ImageDetail | null>(null)
  const [fileOver, setFileOver] = useState(false)
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
    if (confirmingDelete || imageToDelete || picking) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [confirmingDelete, imageToDelete, picking, onClose])

  function closePicker() {
    setPicking(null)
    dialogRef.current?.focus()
  }

  function show(widget: 'checklist' | 'links') {
    setReveal((r) => ({ ...r, [widget]: true }))
    const ref = widget === 'checklist' ? checklistRef : linkRef
    requestAnimationFrame(() => ref.current?.focus())
  }

  return (
    <div className="expanded-backdrop" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <article
        ref={dialogRef}
        className={`expanded fill-${note.color.toLowerCase()}${fileOver ? ' file-over' : ''}${readOnly ? ' read-only' : ''}`}
        // Dropped images are attached to this post-it (pasting is handled by the board).
        onDragOver={(e) => {
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          e.stopPropagation()
          e.dataTransfer.dropEffect = 'copy'
          setFileOver(true)
        }}
        onDragLeave={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFileOver(false)
        }}
        onDrop={(e) => {
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          e.stopPropagation()
          setFileOver(false)
          onAddImages(imageFiles(e.dataTransfer))
        }}
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
                  disabled={readOnly}
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
            autoFocus={focusTitle && !readOnly}
            readOnly={readOnly}
            onSave={(title) => title.length > 0 && void onChange({ title })}
          />
          <div className="band-row">
            <TagChips
              tags={note.tags}
              onRemove={(tag) => void onChange({ tags: note.tags.filter((t) => t !== tag) })}
              onAdd={(anchor) => setPicking({ kind: 'tags', anchor })}
            />
            <AssigneeChips
              assignees={note.assignees}
              onRemove={(a) => void onChange({ assigneeIds: note.assignees.filter((x) => x !== a).map((x) => x.principalId) })}
              onAdd={(anchor) => setPicking({ kind: 'people', anchor })}
            />
          </div>
          <div className="band-row band-meta">
            {note.createdBy ? <span title={new Date(note.createdAt).toLocaleString()}>Created by {note.createdBy}</span> : <span />}
            <NoteIdButton note={note} />
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
                readOnly={readOnly}
                placeholder="Add details: context, decisions, anything that doesn't fit on the post-it."
                inputRef={bodyRef}
                onSave={(body) => void onChange({ body })}
              />
            </section>
            {(note.checklist.length > 0 || reveal.checklist) && (
              <ChecklistWidget items={note.checklist} inputRef={checklistRef} readOnly={readOnly} onChange={(checklist) => onChange({ checklist })} />
            )}
            {(note.links.length > 0 || reveal.links) && (
              <LinksWidget note={note} notes={notes} inputRef={linkRef} onChange={(links) => onChange({ links })} />
            )}
            {images.length > 0 && <ImagesWidget images={images} onDelete={setImageToDelete} />}
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
            <button type="button" className="dock-button" onClick={onPickImages}>
              <ImageIcon size={16} />
              Image
            </button>
            <span className="dock-divider" aria-hidden="true" />
            <span className="dock-hint">Paste or drop anywhere</span>
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

      {picking?.kind === 'tags' && (
        <TagPicker
          anchor={picking.anchor}
          tags={note.tags}
          boardTags={boardTags}
          onChange={(tags) => onChange({ tags })}
          onRename={onRenameTag}
          onDelete={onDeleteTag}
          onClose={closePicker}
        />
      )}
      {picking?.kind === 'people' && (
        <AssigneePicker
          anchor={picking.anchor}
          assignees={note.assignees}
          people={people}
          onChange={(assigneeIds) => onChange({ assigneeIds })}
          onClose={closePicker}
        />
      )}

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

      {imageToDelete && (
        <ConfirmDialog
          title="Delete this image?"
          message={`"${imageToDelete.caption ?? imageToDelete.fileName}" will be removed from this post-it. This can't be undone.`}
          confirmLabel="Delete"
          onConfirm={() => {
            setImageToDelete(null)
            onDeleteImage(imageToDelete.id)
            dialogRef.current?.focus()
          }}
          onCancel={() => {
            setImageToDelete(null)
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
  readOnly?: boolean
  inputRef?: RefObject<HTMLTextAreaElement | null>
}

/**
 * A growing text area that saves on blur (and on Enter unless multiline). Escape cancels.
 * While it isn't focused it follows the server's value, so edits made by agents show up.
 */
function EditableText({ value, onSave, label, className, maxLength, multiline, placeholder, autoFocus, readOnly, inputRef }: EditableTextProps) {
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
      placeholder={readOnly ? undefined : placeholder}
      autoFocus={autoFocus}
      readOnly={readOnly}
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

interface TagChipsProps {
  tags: string[]
  onRemove: (tag: string) => void
  /** Opens the tag window next to the button. */
  onAdd: (anchor: DOMRect) => void
}

function TagChips({ tags, onRemove, onAdd }: TagChipsProps) {
  return (
    <div className="band-tags">
      {tags.map((tag) => (
        <span key={tag} className="band-tag">
          {tag}
          <button type="button" className="chip-remove" aria-label={`Remove tag ${tag}`} onClick={() => onRemove(tag)}>
            <CloseIcon size={10} />
          </button>
        </span>
      ))}
      <button type="button" className="band-add" aria-haspopup="dialog" onClick={(e) => onAdd(e.currentTarget.getBoundingClientRect())}>
        {tags.length < MAX_TAGS ? '+ tag' : 'tags'}
      </button>
    </div>
  )
}

interface AssigneeChipsProps {
  assignees: Assignee[]
  onRemove: (assignee: Assignee) => void
  /** Opens the people window next to the button. */
  onAdd: (anchor: DOMRect) => void
}

function AssigneeChips({ assignees, onRemove, onAdd }: AssigneeChipsProps) {
  return (
    <div className="band-assignees">
      {assignees.map((a) => (
        <span key={a.principalId} className="band-assignee">
          <Avatar assignee={a} />
          {a.name}
          <button type="button" className="chip-remove" aria-label={`Unassign ${a.name}`} onClick={() => onRemove(a)}>
            <CloseIcon size={10} />
          </button>
        </span>
      ))}
      <button type="button" className="band-add" aria-haspopup="dialog" onClick={(e) => onAdd(e.currentTarget.getBoundingClientRect())}>
        {assignees.length < MAX_ASSIGNEES ? '+ assign' : 'people'}
      </button>
    </div>
  )
}

interface ChecklistWidgetProps {
  items: ChecklistItem[]
  inputRef: RefObject<HTMLInputElement | null>
  readOnly?: boolean
  onChange: (items: ChecklistItem[]) => Promise<boolean>
}

function ChecklistWidget({ items, inputRef, readOnly, onChange }: ChecklistWidgetProps) {
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
                disabled={readOnly}
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
  note: NoteDetail
  notes: NoteDetail[]
  inputRef: RefObject<HTMLInputElement | null>
  onChange: (links: NoteLink[]) => Promise<boolean>
}

/** Links to web pages and to other post-its. The address field takes a URL or a post-it's link or ID, or finds post-its by title. */
function LinksWidget({ note, notes, inputRef, onChange }: LinksWidgetProps) {
  const links = note.links
  const [url, setUrl] = useState('')
  const [title, setTitle] = useState('')
  const [invalid, setInvalid] = useState(false)
  const query = url.trim()
  // Typing words (not an address) looks for post-its on this board to link.
  const searching = query.length >= 2 && !/^(https?:|\/#)/i.test(query)
  const linked = new Set(links.map((l) => noteRoute(l.url)?.noteId))
  const matches = searching
    ? notes
        .filter((n) => n.id !== note.id && !linked.has(n.id))
        .filter((n) => n.title.toLowerCase().includes(query.toLowerCase()) || n.id.startsWith(query.toLowerCase()))
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
    const found = notes.find((n) => n.id === target.noteId)
    return addLink({ title: title.trim() || found?.title || 'Post-it', url: notePath(target) })
  }

  async function add(e: FormEvent) {
    e.preventDefault()
    const route = noteRoute(query) ?? (isNoteId(query) ? { boardId: note.boardId, noteId: query.toLowerCase() } : null)
    if (route) return addNote(route)
    if (matches.length > 0) return addNote({ boardId: matches[0].boardId, noteId: matches[0].id })
    const parsed = webUrl(query)
    if (!parsed) {
      setInvalid(true)
      return
    }
    await addLink({ title: title.trim() || `${parsed.host}${parsed.pathname === '/' ? '' : parsed.pathname}`, url: parsed.href })
  }

  return (
    <section className="widget">
      <h2>Links</h2>
      <ul className="links">
        {links.map((link, i) => (
          <li key={i}>
            <LinkRow link={link} notes={notes} />
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
          ref={inputRef}
          aria-label="Link address, or a post-it to link"
          aria-invalid={invalid}
          placeholder="https://… or a post-it"
          value={url}
          maxLength={2000}
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
              <button type="button" className="link-row" onClick={() => void addNote({ boardId: n.boardId, noteId: n.id })}>
                <span className={`link-swatch fill-${n.color.toLowerCase()}`} aria-hidden="true" />
                <span className="link-text">
                  <span className="link-title">{n.title}</span>
                  <span className="link-meta">{n.id}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {invalid && <p className="widget-error">Use a full http or https address, or a post-it's title, link or ID.</p>}
    </section>
  )
}

/** One link: a web page opens in a new tab; a post-it opens here, shown with its current title and fill. */
function LinkRow({ link, notes }: { link: NoteLink; notes: NoteDetail[] }) {
  const route = noteRoute(link.url)
  if (route) {
    const target = notes.find((n) => n.id === route.noteId)
    return (
      // Only the hash changes: the board opens the post-it, or App switches to its board.
      <a className="link-row" href={notePath(route).slice(1)}>
        {target ? <span className={`link-swatch fill-${target.color.toLowerCase()}`} aria-hidden="true" /> : <PostItIcon size={16} />}
        <span className="link-text">
          <span className="link-title">{target?.title ?? link.title}</span>
          <span className="link-meta">{target ? route.noteId : `On another board · ${route.noteId}`}</span>
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
        <span className="link-title">{link.title}</span>
        <span className="link-meta">{parsed ? `${parsed.host}${parsed.pathname}` : link.url}</span>
      </span>
    </a>
  )
}

/** The post-it's ID. Clicking it copies a link that opens this post-it. */
function NoteIdButton({ note }: { note: NoteDetail }) {
  /** Whether the last copy worked; null shows the ID again. */
  const [copied, setCopied] = useState<boolean | null>(null)

  useEffect(() => {
    if (copied === null) return
    const timer = setTimeout(() => setCopied(null), 1600)
    return () => clearTimeout(timer)
  }, [copied])

  return (
    <button
      type="button"
      className="note-id"
      title="Copy a link to this post-it"
      onClick={async () => setCopied(await copyText(`${window.location.origin}${note.path}`))}
    >
      {copied ? <CheckIcon size={12} /> : <LinkIcon size={12} />}
      <span aria-live="polite">{copied === null ? note.id : copied ? 'Link copied' : "Couldn't copy the link"}</span>
    </button>
  )
}

/** Images attached to the post-it. Click one to open the original. */
function ImagesWidget({ images, onDelete }: { images: ImageDetail[]; onDelete: (image: ImageDetail) => void }) {
  return (
    <section className="widget widget-images">
      <div className="widget-head">
        <h2>Images</h2>
        <span className="mono">{images.length}</span>
      </div>
      <ul className="attached-images">
        {images.map((image) => {
          const label = image.caption ?? image.fileName
          return (
            <li key={image.id}>
              <a href={image.contentUrl} target="_blank" rel="noreferrer" className="attached-image" title="Open the original">
                <img src={image.contentUrl} alt={label} loading="lazy" />
                {image.contentType === 'image/gif' && <span className="gif-badge">GIF</span>}
              </a>
              <div className="attached-caption">
                <span className="attached-name">{label}</span>
                <span className="attached-by" title={`Added by ${image.addedBy}`}>
                  added by {image.addedBy}
                </span>
                <button type="button" className="row-remove" aria-label={`Delete ${label}`} onClick={() => onDelete(image)}>
                  <CloseIcon size={12} />
                </button>
              </div>
            </li>
          )
        })}
      </ul>
    </section>
  )
}
