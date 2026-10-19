import type { ComponentType } from 'react'
import { noteColors, type NoteColor } from '../api/types'
import { FrameIcon, ImageIcon, PanIcon, PostItIcon, RulesIcon, SelectIcon } from './icons'
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
  /** Opens the board's agent rules. */
  onOpenRules: () => void
  /** Whether the board has agent rules (shows a dot on the button). */
  hasRules: boolean
  /** Viewers only get Select, Pan and the rules. */
  readOnly?: boolean
}

/** The floating tool rail on the left of the board. */
export function ToolRail({ tool, onToolChange, fill, onFillChange, onAddImage, onOpenRules, hasRules, readOnly }: ToolRailProps) {
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
      {!readOnly && (
        <>
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
        </>
      )}
      <span className="rail-divider" aria-hidden="true" />
      <button
        type="button"
        className={`rail-button${hasRules ? ' has-dot' : ''}`}
        aria-label={hasRules ? 'Agent rules' : 'Agent rules (none yet)'}
        title="Agent rules: what this board is about and how agents should handle its notes"
        onClick={onOpenRules}
      >
        <RulesIcon />
      </button>
    </nav>
  )
}
