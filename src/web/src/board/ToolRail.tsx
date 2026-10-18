import type { ComponentType } from 'react'
import { noteColors, type NoteColor } from '../api/types'
import { FrameIcon, ImageIcon, PanIcon, PostItIcon, SelectIcon } from './icons'
import type { Tool } from './tools'

interface ToolDef {
  tool: Tool
  label: string
  shortcut: string
  Icon: ComponentType
}

const navigation: ToolDef[] = [
  { tool: 'select', label: 'Select', shortcut: 'V', Icon: SelectIcon },
  { tool: 'pan', label: 'Pan', shortcut: 'H', Icon: PanIcon },
]

interface ToolRailProps {
  tool: Tool
  onToolChange: (tool: Tool) => void
  /** Fill for new post-its. */
  fill: NoteColor
  onFillChange: (fill: NoteColor) => void
  /** Opens the file picker to add images (an action, not a tool you stay in). */
  onAddImage: () => void
}

/** The floating tool rail on the left of the board. */
export function ToolRail({ tool, onToolChange, fill, onFillChange, onAddImage }: ToolRailProps) {
  const button = ({ tool: t, label, shortcut, Icon }: ToolDef, className = '') => (
    <button
      key={t}
      type="button"
      className={`rail-button ${className}`}
      aria-label={label}
      title={`${label} (${shortcut})`}
      aria-pressed={tool === t}
      onClick={() => onToolChange(t)}
    >
      <Icon />
    </button>
  )

  return (
    <nav className="tool-rail" aria-label="Tools">
      {navigation.map((t) => button(t))}
      <span className="rail-divider" aria-hidden="true" />
      <div className="rail-note">
        {button({ tool: 'note', label: 'Post-it', shortcut: 'N', Icon: PostItIcon }, `tone-${fill.toLowerCase()}`)}
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
    </nav>
  )
}
