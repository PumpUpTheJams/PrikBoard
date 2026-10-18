import { useState, type FormEvent, type ReactNode } from 'react'
import {
  bucketColors,
  noteColors,
  type BucketChanges,
  type BucketDetail,
  type NoteChanges,
  type NoteDetail,
} from '../api/types'
import { CloseIcon } from './icons'

interface PanelProps {
  title: string
  label: string
  onClose: () => void
  children: ReactNode
}

function Panel({ title, label, onClose, children }: PanelProps) {
  return (
    <aside className="inspector" aria-label={label}>
      <header className="inspector-header">
        <h2>{title}</h2>
        <button type="button" className="icon-button" aria-label="Close" onClick={onClose}>
          <CloseIcon />
        </button>
      </header>
      {children}
    </aside>
  )
}

interface NoteInspectorProps {
  note: NoteDetail
  bucket: BucketDetail | undefined
  /** Focus and select the title, for a post-it that was just stuck on. */
  autoFocus: boolean
  onSave: (changes: NoteChanges) => Promise<boolean>
  onDelete: () => void
  onClose: () => void
}

export function NoteInspector({ note, bucket, autoFocus, onSave, onDelete, onClose }: NoteInspectorProps) {
  const [title, setTitle] = useState(note.title)
  const [body, setBody] = useState(note.body ?? '')
  const dirty = title.trim() !== note.title || body !== (note.body ?? '')

  function save(event: FormEvent) {
    event.preventDefault()
    if (title.trim()) void onSave({ title: title.trim(), body })
  }

  return (
    <Panel title="Post-it" label="Post-it details" onClose={onClose}>
      <p className="inspector-meta">{bucket ? `In ${bucket.name}` : 'Loose on the board'}</p>
      <form className="inspector-form" onSubmit={save}>
        <label>
          Title
          <input
            value={title}
            autoFocus={autoFocus}
            onFocus={(e) => autoFocus && e.target.select()}
            onChange={(e) => setTitle(e.target.value)}
          />
        </label>
        <label>
          Details
          <textarea value={body} rows={6} onChange={(e) => setBody(e.target.value)} />
        </label>
        <fieldset className="swatches">
          <legend>Colour</legend>
          {noteColors.map((c) => (
            <button
              key={c}
              type="button"
              className={`swatch note-${c.toLowerCase()}`}
              title={c}
              aria-label={c}
              aria-pressed={c === note.color}
              onClick={() => void onSave({ color: c })}
            />
          ))}
        </fieldset>
        <div className="inspector-actions">
          <button type="submit" className="primary" disabled={!dirty || !title.trim()}>
            Save
          </button>
          <button type="button" className="danger" onClick={onDelete}>
            Delete
          </button>
        </div>
      </form>
    </Panel>
  )
}

interface BucketInspectorProps {
  bucket: BucketDetail
  noteCount: number
  autoFocus: boolean
  onSave: (changes: BucketChanges) => Promise<boolean>
  onDelete: () => void
  onClose: () => void
}

export function BucketInspector({ bucket, noteCount, autoFocus, onSave, onDelete, onClose }: BucketInspectorProps) {
  const [name, setName] = useState(bucket.name)
  const dirty = name.trim() !== bucket.name

  function save(event: FormEvent) {
    event.preventDefault()
    if (name.trim()) void onSave({ name: name.trim() })
  }

  return (
    <Panel title="Bucket" label="Bucket details" onClose={onClose}>
      <p className="inspector-meta">
        {noteCount === 1 ? '1 post-it' : `${noteCount} post-its`} · {bucket.width}×{bucket.height}
      </p>
      <form className="inspector-form" onSubmit={save}>
        <label>
          Name
          <input
            value={name}
            autoFocus={autoFocus}
            onFocus={(e) => autoFocus && e.target.select()}
            onChange={(e) => setName(e.target.value)}
          />
        </label>
        <fieldset className="swatches">
          <legend>Colour</legend>
          {bucketColors.map((c) => (
            <button
              key={c}
              type="button"
              className={`swatch bucket-${c.toLowerCase()}`}
              title={c}
              aria-label={c}
              aria-pressed={c === bucket.color}
              onClick={() => void onSave({ color: c })}
            />
          ))}
        </fieldset>
        <div className="inspector-actions">
          <button type="submit" className="primary" disabled={!dirty || !name.trim()}>
            Save
          </button>
          <button type="button" className="danger" onClick={onDelete}>
            Delete bucket
          </button>
        </div>
        <p className="inspector-note">Deleting a bucket keeps its post-its on the board.</p>
      </form>
    </Panel>
  )
}
