import type { CSSProperties } from 'react'
import type { InkColor, CardComponentKind } from '../api/types'
import { inkHex } from './drawings'
import { PenOptions } from './DrawingView'
import { ImageIcon, PenIcon, TrashIcon } from './icons'
import { componentKinds } from './componentKinds'

/** The card's pen: whether it is on, and its ink and size. */
export interface CardPen {
  on: boolean
  onToggle: () => void
  ink: InkColor
  onInkChange: (ink: InkColor) => void
  width: number
  onWidthChange: (width: number) => void
}

interface CardBarProps {
  /** Adds a component of a kind in a free spot in view. */
  onAdd: (kind: CardComponentKind) => void
  /** Opens the file picker to attach images. */
  onAddImage: () => void
  pen: CardPen
  /** Asks to delete the whole card. */
  onDelete: () => void
}

/** The expanded card's tool bar, styled like the board's tool rail but lying along the bottom. */
export function CardBar({ onAdd, onAddImage, pen, onDelete }: CardBarProps) {
  return (
    <div className="rail card-bar" role="toolbar" aria-label="Card tools">
      {(['Text', 'Checklist', 'Links'] as const).map((kind) => {
        const { label, Icon } = componentKinds[kind]
        return (
          <button key={kind} type="button" className="rail-button" aria-label={`Add ${label.toLowerCase()}`} title={`Add ${label.toLowerCase()}`} onClick={() => onAdd(kind)}>
            <Icon size={18} />
          </button>
        )
      })}
      <button type="button" className="rail-button" aria-label="Add image" title="Add image, or paste or drop one here" onClick={onAddImage}>
        <ImageIcon />
      </button>
      <div className="card-pen">
        <button
          type="button"
          className="rail-button pen-button"
          aria-label="Pen"
          title="Pen (P): draw on the card"
          aria-pressed={pen.on}
          style={{ '--pen-ink': inkHex[pen.ink] } as CSSProperties}
          onClick={pen.onToggle}
        >
          <PenIcon />
        </button>
        {pen.on && (
          <div className="card-pen-flyout" role="group" aria-label="Pen color and size">
            <PenOptions ink={pen.ink} onInkChange={pen.onInkChange} width={pen.width} onWidthChange={pen.onWidthChange} />
          </div>
        )}
      </div>
      <span className="rail-divider" aria-hidden="true" />
      <button type="button" className="rail-button danger" aria-label="Delete card" title="Delete card" onClick={onDelete}>
        <TrashIcon size={17} />
      </button>
    </div>
  )
}
