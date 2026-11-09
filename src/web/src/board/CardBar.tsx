import type { CSSProperties } from 'react'
import type { InkColor, CardComponentKind } from '../api/types'
import { inkColor } from './drawings'
import { PenOptions } from './DrawingView'
import { EraserIcon, HandIcon, ImageIcon, LabelIcon, PenIcon, SelectIcon, TrashIcon } from './icons'
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

/** The card's label tool: whether it is on (the next click adds a label). */
export interface CardLabelTool {
  on: boolean
  onToggle: () => void
}

/** How a press on the card's canvas works: selects and moves (`select`), only moves the view (`hand`), or does what the pen or label tool does. */
export interface CardNavigation {
  mode: 'select' | 'hand' | 'tool'
  onSelect: () => void
  onHand: () => void
}

interface CardBarProps {
  navigation: CardNavigation
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
export function CardBar({ navigation, onAdd, onAddImage, pen, label, onDelete }: CardBarProps) {
  return (
    <div className="rail card-bar" role="toolbar" aria-label="Card tools">
      <button type="button" className="rail-button" aria-label="Select" title="Select (V)" aria-pressed={navigation.mode === 'select'} onClick={navigation.onSelect}>
        <SelectIcon />
      </button>
      <button type="button" className="rail-button" aria-label="Hand" title="Hand (H): drag to move around, scroll to zoom" aria-pressed={navigation.mode === 'hand'} onClick={navigation.onHand}>
        <HandIcon />
      </button>
      <span className="rail-divider" aria-hidden="true" />
      {(['Checklist', 'Links'] as const).map((kind) => {
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
      </div>
      <span className="rail-divider" aria-hidden="true" />
      <button type="button" className="rail-button danger" aria-label="Delete card" title="Delete card" onClick={onDelete}>
        <TrashIcon size={17} />
      </button>
    </div>
  )
}
