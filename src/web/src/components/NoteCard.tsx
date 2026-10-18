import { useState, type FormEvent } from 'react'
import { noteColors, type NoteChanges, type NoteDetail } from '../api/types'

interface NoteCardProps {
  note: NoteDetail
  onSave: (changes: NoteChanges) => Promise<boolean>
  onDelete: () => void
  onDragStart: () => void
  onDragEnd: () => void
  /** Another note was dropped on this card: insert it just above this one. */
  onDropBefore: () => void
}

export function NoteCard({ note, onSave, onDelete, onDragStart, onDragEnd, onDropBefore }: NoteCardProps) {
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState(note.title)
  const [body, setBody] = useState(note.body ?? '')
  const [color, setColor] = useState(note.color)

  function startEditing() {
    setTitle(note.title)
    setBody(note.body ?? '')
    setColor(note.color)
    setEditing(true)
  }

  async function save(event: FormEvent) {
    event.preventDefault()
    if (!title.trim()) return
    if (await onSave({ title: title.trim(), body, color })) setEditing(false)
  }

  if (editing) {
    return (
      <form className={`note note-${color.toLowerCase()} editing`} onSubmit={save}>
        <input autoFocus value={title} aria-label="Title" onChange={(e) => setTitle(e.target.value)} />
        <textarea
          value={body}
          rows={4}
          placeholder="Details"
          aria-label="Details"
          onChange={(e) => setBody(e.target.value)}
        />
        <div className="swatches" aria-label="Colour">
          {noteColors.map((c) => (
            <button
              key={c}
              type="button"
              className={`swatch note-${c.toLowerCase()}`}
              title={c}
              aria-label={c}
              aria-pressed={c === color}
              onClick={() => setColor(c)}
            />
          ))}
        </div>
        <div className="note-actions">
          <button type="submit" disabled={!title.trim()}>
            Save
          </button>
          <button type="button" className="ghost" onClick={() => setEditing(false)}>
            Cancel
          </button>
          <button type="button" className="danger" onClick={onDelete}>
            Delete
          </button>
        </div>
      </form>
    )
  }

  return (
    <article
      className={`note note-${note.color.toLowerCase()}`}
      tabIndex={0}
      draggable
      title="Click to edit, drag to move"
      onClick={startEditing}
      onKeyDown={(e) => e.key === 'Enter' && startEditing()}
      onDragStart={(e) => {
        e.dataTransfer.setData('text/plain', note.id)
        e.dataTransfer.effectAllowed = 'move'
        onDragStart()
      }}
      onDragEnd={onDragEnd}
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault()
        e.stopPropagation()
        onDropBefore()
      }}
    >
      <h3>{note.title}</h3>
      {note.body && <p>{note.body}</p>}
    </article>
  )
}
