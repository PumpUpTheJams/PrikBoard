import type { ComponentType, CSSProperties, PointerEvent, Ref } from 'react'
import { cardColors, type InkColor, type CardColor } from '../api/types'
import { inkHex } from './drawings'
import { PenOptions } from './DrawingView'
import { BucketToolIcon, ImageIcon, PenIcon, CardIcon, SearchIcon, SelectIcon, TrashIcon } from './icons'
import type { Tool } from './tools'

interface ToolDef {
  tool: Tool
  label: string
  shortcut: string
  Icon: ComponentType
  /** Tooltip; default the label and shortcut. */
  title?: string
}

const navigation: ToolDef[] = [{ tool: 'select', label: 'Select', shortcut: 'V', Icon: SelectIcon }]

interface ToolRailProps {
  tool: Tool
  onToolChange: (tool: Tool) => void
  /** Fill for new cards. */
  fill: CardColor
  /** A press on the card button: dragging from there adds a new card where it is released. */
  onCardPointerDown?: (e: PointerEvent<HTMLButtonElement>) => void
  /**
   * While a card is dragged (or a new one carried), the card button is a trash can to drop it on; `over` while the
   * pointer is on it. The board hit-tests it through `trashRef`, since it holds the pointer during the drag.
   */
  trash?: 'shown' | 'over'
  trashRef?: Ref<HTMLButtonElement>
  onFillChange: (fill: CardColor) => void
  /** The pen's ink and base line width, picked in its flyout. */
  ink: InkColor
  onInkChange: (ink: InkColor) => void
  strokeWidth: number
  onStrokeWidthChange: (width: number) => void
  /** Opens the file picker to add images (an action, not a tool you stay in). */
  onAddImage: () => void
  /** Viewers only get Select and Search. */
  readOnly?: boolean
  /** Whether the board search is open; the search button opens and closes it. */
  searching: boolean
  onSearch: () => void
}

/** The floating tool rail on the left of the board. */
export function ToolRail(props: ToolRailProps) {
  const { tool, onToolChange, fill, onFillChange, onCardPointerDown, trash, trashRef, onAddImage, readOnly, searching, onSearch } = props
  const { ink, onInkChange, strokeWidth, onStrokeWidthChange } = props
  const button = (
    { tool: t, label, shortcut, Icon, title }: ToolDef,
    className = '',
    onPointerDown?: (e: PointerEvent<HTMLButtonElement>) => void,
    ref?: Ref<HTMLButtonElement>,
    style?: CSSProperties,
  ) => (
    <button
      key={t}
      ref={ref}
      type="button"
      className={`rail-button ${className}`}
      aria-label={label}
      title={title ?? `${label} (${shortcut})`}
      aria-pressed={tool === t}
      style={style}
      onPointerDown={onPointerDown}
      // A second click on the tool you are in puts it away again (as Escape does).
      onClick={() => onToolChange(tool === t && t !== 'select' ? 'select' : t)}
    >
      <Icon />
    </button>
  )

  return (
    <nav className="rail tool-rail" aria-label="Tools">
      {navigation.map((t) => button(t))}
      {!readOnly && (
        <>
          <span className="rail-divider" aria-hidden="true" />
          <div className="rail-anchor">
            {trash
              ? button(
                  { tool: 'card', label: 'Trash', shortcut: 'C', Icon: TrashIcon, title: 'Drop a card here to delete it' },
                  `trash${trash === 'over' ? ' over' : ''}`,
                  onCardPointerDown,
                  trashRef,
                )
              : button(
                  { tool: 'card', label: 'Card', shortcut: 'C', Icon: CardIcon, title: 'Card (C): drag it onto the board, or click here and then on the board' },
                  `tone-${fill.toLowerCase()}`,
                  onCardPointerDown,
                  trashRef,
                )}
            {tool === 'card' && !trash && (
              <div className="fill-flyout" role="group" aria-label="Fill for new cards">
                {cardColors.map((c) => (
                  <button
                    key={c}
                    type="button"
                    className={`swatch fill-${c.toLowerCase()}`}
                    title={c}
                    aria-label={c}
                    aria-pressed={c === fill}
                    onClick={() => onFillChange(c)}
                  />
                ))}
              </div>
            )}
          </div>
          {button({ tool: 'bucket', label: 'Bucket', shortcut: 'B', Icon: BucketToolIcon })}
          <button type="button" className="rail-button" aria-label="Image" title="Image (I), or paste or drop one on the board" onClick={onAddImage}>
            <ImageIcon />
          </button>
          <div className="rail-anchor">
            {button({ tool: 'pen', label: 'Pen', shortcut: 'P', Icon: PenIcon }, 'pen-button', undefined, undefined, { '--pen-ink': inkHex[ink] } as CSSProperties)}
            {tool === 'pen' && (
              <div className="fill-flyout pen-flyout" role="group" aria-label="Pen color and size">
                <PenOptions ink={ink} onInkChange={onInkChange} width={strokeWidth} onWidthChange={onStrokeWidthChange} />
              </div>
            )}
          </div>
        </>
      )}
      <span className="rail-divider" aria-hidden="true" />
      <button type="button" className="rail-button" aria-label="Search" title="Search (/ or Ctrl+F)" aria-pressed={searching} onClick={onSearch}>
        <SearchIcon size={18} />
      </button>
    </nav>
  )
}
