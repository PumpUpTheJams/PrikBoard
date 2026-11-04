import type { CSSProperties } from 'react'
import type { InkColor, CardComponentKind, LabelSize } from '../api/types'
import { inkColor } from './drawings'
import { PenOptions } from './DrawingView'
import { EraserIcon, ImageIcon, LabelIcon, PenIcon, TrashIcon } from './icons'
import { LabelOptions } from './LabelView'
import { componentKinds } from './componentKinds'

/** The card's pen: whether it is on, its ink and size, and whether it erases. */
export interface CardPen {
  on: boolean
  onToggle: () => void
  ink: InkColor
  onInkChange: (ink: InkColor) => void
  width: number
  onWidthChange: (width: number) => void
  eraser: boolean
  onEraserChange: (eraser: boolean) => void
}

/** The card's label tool: whether it is on (the next click adds a label), and the ink and size new labels get. */
export interface CardLabelTool {
  on: boolean
  onToggle: () => void
  ink: InkColor
  onInkChange: (ink: InkColor) => void
  size: LabelSize
  onSizeChange: (size: LabelSize) => void
}

interface CardBarProps {
  /** Adds a component of a kind in a free spot in view. */
  onAdd: (kind: CardComponentKind) => void
  /** Opens the file picker to attach images. */
  onAddImage: () => void
  pen: CardPen
  label: CardLabelTool
  /** Deletes the whole card, without asking first (undo brings it back). */
  onDelete: () => void
}

/** The expanded card's tool bar, styled like the board's tool rail but lying along the bottom. */
export function CardBar({ onAdd, onAddImage, pen, label, onDelete }: CardBarProps) {
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
          style={{ '--pen-ink': inkColor[pen.ink] } as CSSProperties}
          onClick={pen.onToggle}
        >
          {pen.on && pen.eraser ? <EraserIcon /> : <PenIcon />}
        </button>
        {pen.on && (
          <div className="card-pen-flyout" role="group" aria-label="Pen color and size">
            <PenOptions
              ink={pen.ink}
              onInkChange={pen.onInkChange}
              width={pen.width}
              onWidthChange={pen.onWidthChange}
              eraser={pen.eraser}
              onEraserChange={pen.onEraserChange}
            />
          </div>
        )}
      </div>
      <div className="card-pen">
        <button type="button" className="rail-button" aria-label="Label" title="Label (T): click on the card and type" aria-pressed={label.on} onClick={label.onToggle}>
          <LabelIcon />
        </button>
        {label.on && (
          <div className="card-pen-flyout" role="group" aria-label="Label color and size">
            <LabelOptions ink={label.ink} onInkChange={label.onInkChange} size={label.size} onSizeChange={label.onSizeChange} />
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
