import type { CSSProperties } from 'react'
import type { InkColor, NoteBlockKind } from '../api/types'
import { inkHex } from './drawings'
import { PenOptions } from './DrawingView'
import { ImageIcon, PenIcon, TrashIcon } from './icons'
import { blockKinds } from './blockKinds'

/** The post-it's pen: whether it is on, and its ink and size. */
export interface NotePen {
  on: boolean
  onToggle: () => void
  ink: InkColor
  onInkChange: (ink: InkColor) => void
  width: number
  onWidthChange: (width: number) => void
}

interface NoteBarProps {
  /** Adds a block of a kind in a free spot in view. */
  onAdd: (kind: NoteBlockKind) => void
  /** Opens the file picker to attach images. */
  onAddImage: () => void
  pen: NotePen
  /** Asks to delete the whole post-it. */
  onDelete: () => void
}

/** The expanded post-it's tool bar, styled like the board's tool rail but lying along the bottom. */
export function NoteBar({ onAdd, onAddImage, pen, onDelete }: NoteBarProps) {
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
      <div className="note-pen">
        <button
          type="button"
          className="rail-button note-pen-button"
          aria-label="Pen"
          title="Pen (P): draw on the post-it"
          aria-pressed={pen.on}
          style={{ '--pen-ink': inkHex[pen.ink] } as CSSProperties}
          onClick={pen.onToggle}
        >
          <PenIcon />
        </button>
        {pen.on && (
          <div className="note-pen-flyout" role="group" aria-label="Pen colour and size">
            <PenOptions ink={pen.ink} onInkChange={pen.onInkChange} width={pen.width} onWidthChange={pen.onWidthChange} />
          </div>
        )}
      </div>
      <span className="rail-divider" aria-hidden="true" />
      <button type="button" className="rail-button danger" aria-label="Delete post-it" title="Delete post-it" onClick={onDelete}>
        <TrashIcon size={17} />
      </button>
    </div>
  )
}
