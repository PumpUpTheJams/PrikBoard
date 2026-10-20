import { useEffect, useImperativeHandle, useRef, useState, type RefObject } from 'react'
import {
  noteColors,
  type Assignee,
  type BoardPerson,
  type BoardTag,
  type BucketDetail,
  type DrawingDetail,
  type ImageDetail,
  type NoteChanges,
  type NoteColor,
  type NoteDetail,
  type Point,
  type TagColor,
} from '../api/types'
import { Avatar } from '../components/Avatar'
import { AssigneePicker } from './AssigneePicker'
import { EditableText } from './EditableText'
import { draggingFiles, imageFiles } from './files'
import { Highlight } from './Highlight'
import { BucketIcon, CheckIcon, CloseIcon, LinkIcon, PutBackIcon, SparkleIcon, TrashIcon } from './icons'
import { noteLabel, TITLE_SUGGESTION } from './links'
import { pop } from './motion'
import { NoteCanvas, type IncomingDrawing, type IncomingImage, type NoteCanvasHandle } from './NoteCanvas'
import { tagColorClass, type TagColorPreview, type TagColors } from './tagColors'
import { TagPicker } from './TagPicker'
import type { RecordingRun } from './undo'
import { MAX_ASSIGNEES, MAX_TAGS } from './tags'
import { useCopyLink } from './useCopyLink'
import { useFloatingWindow } from './useFloatingWindow'

/** What the board needs from the open post-it: its canvas, and whether a paste is meant for it. */
export interface ExpandedNoteHandle extends NoteCanvasHandle {
  /** The pointer is over the window, or focus is in it. */
  wantsPaste: () => boolean
  /** Whether a screen point is over the window. */
  contains: (client: Point) => boolean
  /** A little pop of the window, when a post-it dropped on it was linked there. */
  pop: () => void
}

interface ExpandedNoteProps {
  note: NoteDetail
  bucket: BucketDetail | undefined
  /** Focus and select the title, for a post-it that was just stuck on. */
  focusTitle: boolean
  onChange: (changes: NoteChanges) => Promise<boolean>
  /** The board's tags and the people who can be assigned, to pick from. */
  boardTags: BoardTag[]
  /** How each tag shows, including a colour being previewed. */
  tagColors: TagColors
  people: BoardPerson[]
  onRenameTag: (tag: BoardTag, name: string) => Promise<boolean>
  onRecolorTag: (tag: BoardTag, color: TagColor) => Promise<boolean>
  onPreviewTagColor: (preview: TagColorPreview | null) => void
  onDeleteTag: (tag: BoardTag) => Promise<boolean>
  /** Deletes the post-it (undone with the toast's Undo button or Ctrl/Cmd+Z, so nothing asks first). */
  onDelete: () => void
  /** Images attached to this post-it. */
  images: ImageDetail[]
  /** Pen strokes on this post-it (none for one in the trash). */
  drawings?: DrawingDetail[]
  /** The post-its on the board, to link to and to show linked ones by their current title. */
  notes: NoteDetail[]
  /** Attaches images, at a point on the post-it's canvas or in a free spot. */
  onAddImages: (files: File[], at?: Point) => void
  onPickImages: () => void
  onClose: () => void
  /** Viewers see the post-it without any way to change it (the API refuses changes anyway). */
  readOnly?: boolean
  /** Words the board search found on it, to mark (opened from the search results). */
  highlight?: string[]
  handle: RefObject<ExpandedNoteHandle | null>
  /** Runs a change through the board (which then reloads), recorded for undo. */
  run: RecordingRun
  /** Whether a drag on the post-it's canvas is in progress, so the board holds back its refreshes. */
  onBusy: (busy: boolean) => void
  /** A board image being dragged over the window. */
  incoming: IncomingImage | null
  /** A board drawing being dragged over the window. */
  incomingDrawing?: IncomingDrawing | null
  /** A post-it is being dragged over the window, to link it here. */
  linking: boolean
  /** A screen point on the board in canvas pixels, when it is over the board; to drop images out of the post-it. */
  boardPoint: (client: Point) => Point | null
  boardZoom: number
  /** For a post-it in the trash (shown read only): putting it back on the board (not for viewers). */
  deleted?: { onPutBack?: () => void }
}

/**
 * A post-it opened up in a window floating over the board: a header band in its fill, and the post-it's own small board
 * with its blocks and images. The board stays usable, and fully visible, around it.
 */
export function ExpandedNote(props: ExpandedNoteProps) {
  const { note, bucket, images, focusTitle, onChange, onDelete, onAddImages, onPickImages, onClose, readOnly, highlight } = props
  const { boardTags, tagColors, people, notes, onRenameTag, onRecolorTag, onPreviewTagColor, onDeleteTag, handle, run, onBusy, incoming, linking, boardPoint, boardZoom, deleted } = props
  const { drawings = [], incomingDrawing = null } = props
  /** The tag or people window, opened from its button in the band. */
  const [picking, setPicking] = useState<{ kind: 'tags' | 'people'; anchor: DOMRect } | null>(null)
  const [fileOver, setFileOver] = useState(false)
  /** Whether the pointer is over the window: a paste then goes to the post-it. */
  const [hover, setHover] = useState(true)
  const dialogRef = useRef<HTMLElement>(null)
  const canvas = useRef<NoteCanvasHandle>(null)
  const titleRef = useRef<HTMLInputElement & HTMLTextAreaElement>(null)
  const { rect, moving, startMove, startResize } = useFloatingWindow(dialogRef)

  // Focus goes to the window, or for a post-it just stuck on the board to its title with the text selected, so typing
  // replaces it. Only once the window shows: it is hidden until it has measured the board, and hidden things can't
  // take focus.
  const shown = rect !== null
  useEffect(() => {
    if (!shown) return
    if (focusTitle && !readOnly && titleRef.current) {
      titleRef.current.focus({ preventScroll: true })
      titleRef.current.select()
    } else {
      dialogRef.current?.focus({ preventScroll: true })
    }
  }, [shown, focusTitle, readOnly])

  // Escape closes the post-it wherever focus is (it can end up on the page after saving the title). Fields and the
  // pickers handle their own Escape first and stop it from getting here.
  useEffect(() => {
    if (picking) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [picking, onClose])

  // Follows the pointer everywhere (also while the board or the canvas holds it during a drag).
  useEffect(() => {
    function onMove(e: PointerEvent) {
      const r = dialogRef.current?.getBoundingClientRect()
      setHover(!!r && e.clientX >= r.left && e.clientX < r.right && e.clientY >= r.top && e.clientY < r.bottom)
    }
    window.addEventListener('pointermove', onMove)
    return () => window.removeEventListener('pointermove', onMove)
  }, [])

  useImperativeHandle(handle, () => ({
    hits: (client) => canvas.current?.hits(client) ?? false,
    toNote: (client) => canvas.current!.toNote(client),
    pastePoint: () => canvas.current!.pastePoint(),
    wantsPaste: () => hover || !!dialogRef.current?.contains(document.activeElement),
    contains: (client) => {
      const r = dialogRef.current?.getBoundingClientRect()
      return !!r && client.x >= r.left && client.x < r.right && client.y >= r.top && client.y < r.bottom
    },
    pop: () => pop(dialogRef.current, 1.015),
  }))

  function closePicker() {
    setPicking(null)
    dialogRef.current?.focus({ preventScroll: true })
  }

  return (
    <>
      <article
        ref={dialogRef}
        className={`expanded fill-${note.color.toLowerCase()}${moving ? ' moving' : ''}${fileOver ? ' file-over' : ''}${readOnly ? ' read-only' : ''}${incoming || incomingDrawing || linking ? ' drop-over' : ''}`}
        style={rect ? { left: rect.x, top: rect.y, width: rect.width, height: rect.height } : { visibility: 'hidden' }}
        // Images dropped on the band are attached in a free spot (the canvas takes drops where they land).
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
          setFileOver(false)
          if (readOnly || !draggingFiles(e.dataTransfer)) return
          e.preventDefault()
          e.stopPropagation()
          onAddImages(imageFiles(e.dataTransfer))
        }}
        role="dialog"
        aria-modal="false"
        aria-label={noteLabel(note)}
        tabIndex={-1}
      >
        <header className="band" onPointerDown={startMove}>
          <div className="band-row">
            {deleted ? (
              <span className="band-bucket">
                <TrashIcon size={14} />
                Deleted
              </span>
            ) : (
              <span className="band-bucket">
                <BucketIcon />
                {bucket ? bucket.name : 'Loose on the board'}
              </span>
            )}
            <div className="band-actions">
              {deleted?.onPutBack && (
                <button type="button" className="band-button" title="Put back where it was on the board" onClick={deleted.onPutBack}>
                  <PutBackIcon />
                  Put back
                </button>
              )}
              {!deleted && <FillPicker value={note.color} onChange={(color) => void onChange({ color })} />}
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
              <button type="button" className="band-button icon" aria-label="Close" title="Close (Esc)" onClick={onClose}>
                <CloseIcon />
              </button>
            </div>
          </div>
          <EditableText
            className="band-title"
            label="Title"
            value={note.title}
            maxLength={200}
            placeholder={TITLE_SUGGESTION}
            fieldRef={titleRef}
            readOnly={readOnly}
            onSave={(title) => void onChange({ title })}
          />
          <div className="band-row">
            <TagChips
              tags={note.tags}
              tagColors={tagColors}
              highlight={highlight}
              onRemove={(tag) => void onChange({ tags: note.tags.filter((t) => t !== tag) })}
              onAdd={(anchor) => setPicking({ kind: 'tags', anchor })}
            />
            <AssigneeChips
              assignees={note.assignees}
              highlight={highlight}
              onRemove={(a) => void onChange({ assigneeIds: note.assignees.filter((x) => x !== a).map((x) => x.principalId) })}
              onAdd={(anchor) => setPicking({ kind: 'people', anchor })}
            />
          </div>
          <div className="band-row band-meta">
            {note.createdBy ? (
              <span title={new Date(note.createdAt).toLocaleString()}>
                Created by <Highlight text={note.createdBy} terms={highlight} />
              </span>
            ) : (
              <span />
            )}
            <NoteIdButton note={note} />
          </div>
        </header>

        <NoteCanvas
          note={note}
          images={images}
          drawings={drawings}
          notes={notes}
          highlight={highlight}
          readOnly={readOnly}
          handle={canvas}
          run={run}
          onBusy={onBusy}
          incoming={incoming}
          incomingDrawing={incomingDrawing}
          onAddImages={onAddImages}
          onPickImages={onPickImages}
          boardPoint={boardPoint}
          boardZoom={boardZoom}
          onDeletePostIt={onDelete}
        />

        <span className="window-resize e" aria-hidden="true" onPointerDown={startResize('e')} />
        <span className="window-resize s" aria-hidden="true" onPointerDown={startResize('s')} />
        <span className="window-resize se" aria-hidden="true" onPointerDown={startResize('se')} />
      </article>

      {picking?.kind === 'tags' && (
        <TagPicker
          anchor={picking.anchor}
          tags={note.tags}
          boardTags={boardTags}
          tagColors={tagColors}
          onChange={(tags) => onChange({ tags })}
          onRename={onRenameTag}
          onRecolor={onRecolorTag}
          onPreview={onPreviewTagColor}
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
    </>
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
  tagColors: TagColors
  /** Search words to mark. */
  highlight?: string[]
  onRemove: (tag: string) => void
  /** Opens the tag window next to the button. */
  onAdd: (anchor: DOMRect) => void
}

function TagChips({ tags, tagColors, highlight, onRemove, onAdd }: TagChipsProps) {
  return (
    <div className="band-tags">
      {tags.map((tag) => (
        <span key={tag} className={`band-tag${tagColorClass(tagColors, tag)}`}>
          <Highlight text={tag} terms={highlight} />
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
  /** Search words to mark. */
  highlight?: string[]
  onRemove: (assignee: Assignee) => void
  /** Opens the people window next to the button. */
  onAdd: (anchor: DOMRect) => void
}

function AssigneeChips({ assignees, highlight, onRemove, onAdd }: AssigneeChipsProps) {
  return (
    <div className="band-assignees">
      {assignees.map((a) => (
        <span key={a.principalId} className="band-assignee">
          <Avatar assignee={a} />
          <Highlight text={a.name} terms={highlight} />
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

/** The post-it's ID. Clicking it copies a link that opens this post-it. */
function NoteIdButton({ note }: { note: NoteDetail }) {
  /** Whether the last copy worked; null shows the ID again. */
  const { copied, copy } = useCopyLink(note)

  return (
    <button type="button" className="note-id" title="Copy a link to this post-it" onClick={copy}>
      {copied ? <CheckIcon size={12} /> : <LinkIcon size={12} />}
      <span aria-live="polite">{copied === null ? note.readableId : copied ? 'Link copied' : "Couldn't copy the link"}</span>
    </button>
  )
}
