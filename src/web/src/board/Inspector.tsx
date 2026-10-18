import { useState, type FormEvent } from 'react'
import type { BucketChanges, BucketDetail, ImageChanges, ImageDetail } from '../api/types'
import { CloseIcon, ExternalIcon, TrashIcon } from './icons'

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

const fileSize = (bytes: number) => (bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`)

interface ImageInspectorProps {
  image: ImageDetail
  onSave: (changes: ImageChanges) => Promise<boolean>
  /** Asks to delete the image (the canvas confirms first). */
  onDelete: () => void
  onClose: () => void
}

/** A small panel for the selected image: caption it, open the original or delete it. */
export function ImageInspector({ image, onSave, onDelete, onClose }: ImageInspectorProps) {
  const [caption, setCaption] = useState(image.caption ?? '')
  const dirty = caption.trim() !== (image.caption ?? '')

  function save(event: FormEvent) {
    event.preventDefault()
    void onSave({ caption: caption.trim() })
  }

  return (
    <aside className="inspector" aria-label="Image details">
      <header className="inspector-header">
        <h2>Image</h2>
        <span className="inspector-meta">
          {image.pixelWidth}×{image.pixelHeight} · {fileSize(image.size)}
        </span>
        <button type="button" className="icon-button" aria-label="Close" onClick={onClose}>
          <CloseIcon />
        </button>
      </header>
      <p className="inspector-note" title={image.fileName}>
        {image.fileName} · added by {image.addedBy}
      </p>
      <form className="inspector-form" onSubmit={save}>
        <input aria-label="Caption" placeholder="Caption" value={caption} maxLength={500} onChange={(e) => setCaption(e.target.value)} />
        <div className="inspector-actions">
          <button type="submit" className="primary" disabled={!dirty}>
            Save
          </button>
          <a className="ghost inspector-link" href={image.contentUrl} target="_blank" rel="noreferrer">
            <ExternalIcon />
            Original
          </a>
          <button type="button" className="danger" onClick={onDelete}>
            <TrashIcon />
            Delete
          </button>
        </div>
      </form>
    </aside>
  )
}
