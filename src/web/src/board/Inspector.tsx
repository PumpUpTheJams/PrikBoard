import { useState, type FormEvent } from 'react'
import type { BucketChanges, BucketDetail } from '../api/types'
import { CloseIcon, TrashIcon } from './icons'

interface BucketInspectorProps {
  bucket: BucketDetail
  noteCount: number
  /** Focus and select the name, for a bucket that was just drawn. */
  autoFocus: boolean
  onSave: (changes: BucketChanges) => Promise<boolean>
  onDelete: () => void
  onClose: () => void
}

/** A small panel for the selected bucket: rename it or delete it. */
export function BucketInspector({ bucket, noteCount, autoFocus, onSave, onDelete, onClose }: BucketInspectorProps) {
  const [name, setName] = useState(bucket.name)
  const dirty = name.trim() !== bucket.name

  function save(event: FormEvent) {
    event.preventDefault()
    if (name.trim()) void onSave({ name: name.trim() })
  }

  return (
    <aside className="inspector" aria-label="Bucket details">
      <header className="inspector-header">
        <h2>Bucket</h2>
        <span className="inspector-meta">
          {noteCount === 1 ? '1 post-it' : `${noteCount} post-its`} · {bucket.width}×{bucket.height}
        </span>
        <button type="button" className="icon-button" aria-label="Close" onClick={onClose}>
          <CloseIcon />
        </button>
      </header>
      <form className="inspector-form" onSubmit={save}>
        <input
          aria-label="Bucket name"
          value={name}
          maxLength={100}
          autoFocus={autoFocus}
          onFocus={(e) => autoFocus && e.target.select()}
          onChange={(e) => setName(e.target.value)}
        />
        <div className="inspector-actions">
          <button type="submit" className="primary" disabled={!dirty || !name.trim()}>
            Rename
          </button>
          <button type="button" className="danger" onClick={onDelete}>
            <TrashIcon />
            Delete
          </button>
        </div>
        <p className="inspector-note">Deleting a bucket keeps its post-its on the board.</p>
      </form>
    </aside>
  )
}
