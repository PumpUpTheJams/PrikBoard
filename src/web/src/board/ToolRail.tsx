import type { ComponentType, PointerEvent } from 'react'
import { noteColors, type NoteColor } from '../api/types'
import { FrameIcon, ImageIcon, PostItIcon, SelectIcon } from './icons'
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
  /** Fill for new post-its. */
  fill: NoteColor
  /** A press on the post-it button: dragging from there sticks a new post-it where it is released. */
  onPostItPointerDown?: (e: PointerEvent<HTMLButtonElement>) => void
  onFillChange: (fill: NoteColor) => void
  /** Opens the file picker to add images (an action, not a tool you stay in). */
  onAddImage: () => void
  /** Viewers only get Select. */
  readOnly?: boolean
}

/** The floating tool rail on the left of the board. */
export function ToolRail(props: ToolRailProps) {
  const { tool, onToolChange, fill, onFillChange, onPostItPointerDown, onAddImage, readOnly } = props
  const button = ({ tool: t, label, shortcut, Icon, title }: ToolDef, className = '', onPointerDown?: (e: PointerEvent<HTMLButtonElement>) => void) => (
    <button
      key={t}
      type="button"
      className={`rail-button ${className}`}
      aria-label={label}
      title={title ?? `${label} (${shortcut})`}
      aria-pressed={tool === t}
      onPointerDown={onPointerDown}
      onClick={() => onToolChange(t)}
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
          <div className="rail-note">
            {button(
              { tool: 'note', label: 'Post-it', shortcut: 'N', Icon: PostItIcon, title: 'Post-it (N): drag it onto the board, or click here and then on the board' },
              `tone-${fill.toLowerCase()}`,
              onPostItPointerDown,
            )}
            {tool === 'note' && (
              <div className="fill-flyout" role="group" aria-label="Fill for new post-its">
                {noteColors.map((c) => (
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
          {button({ tool: 'bucket', label: 'Bucket', shortcut: 'B', Icon: FrameIcon })}
          <button type="button" className="rail-button" aria-label="Image" title="Image (I), or paste or drop one on the board" onClick={onAddImage}>
            <ImageIcon />
          </button>
        </>
      )}
    </nav>
  )
}
