import type { NoteBlockKind } from '../api/types'
import { ImageIcon, TrashIcon } from './icons'
import { blockKinds } from './blockKinds'

interface NoteBarProps {
  /** Adds a block of a kind in a free spot in view. */
  onAdd: (kind: NoteBlockKind) => void
  /** Opens the file picker to attach images. */
  onAddImage: () => void
  /** Asks to delete the whole post-it. */
  onDelete: () => void
}

/** The expanded post-it's tool bar, styled like the board's tool rail but lying along the bottom. */
export function NoteBar({ onAdd, onAddImage, onDelete }: NoteBarProps) {
  return (
    <div className="rail note-bar" role="toolbar" aria-label="Post-it tools">
      {(['Text', 'Checklist', 'Links'] as const).map((kind) => {
        const { label, Icon } = blockKinds[kind]
        return (
          <button key={kind} type="button" className="rail-button" aria-label={`Add ${label.toLowerCase()}`} title={`Add ${label.toLowerCase()}`} onClick={() => onAdd(kind)}>
            <Icon size={18} />
          </button>
        )
      })}
      <button type="button" className="rail-button" aria-label="Add image" title="Add image, or paste or drop one here" onClick={onAddImage}>
        <ImageIcon />
      </button>
      <span className="rail-divider" aria-hidden="true" />
      <button type="button" className="rail-button danger" aria-label="Delete post-it" title="Delete post-it" onClick={onDelete}>
        <TrashIcon size={17} />
      </button>
    </div>
  )
}
