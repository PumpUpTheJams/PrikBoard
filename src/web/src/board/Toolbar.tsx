import { noteColors, type NoteColor } from '../api/types'
import { BucketIcon, MoveIcon, NoteIcon } from './icons'

export type Tool = 'move' | 'bucket' | 'note'

const tools = [
  { tool: 'move', label: 'Move', Icon: MoveIcon },
  { tool: 'bucket', label: 'Draw bucket', Icon: BucketIcon },
  { tool: 'note', label: 'Post-it', Icon: NoteIcon },
] as const

const hints: Record<Tool, string> = {
  move: 'Drag post-its anywhere; they snap to the grid. Drop one inside a bucket to move it there.',
  bucket: 'Drag across the board to draw a bucket.',
  note: 'Click the board to stick a new post-it.',
}

interface ToolbarProps {
  tool: Tool
  onToolChange: (tool: Tool) => void
  /** Colour for new post-its. */
  color: NoteColor
  onColorChange: (color: NoteColor) => void
}

export function Toolbar({ tool, onToolChange, color, onColorChange }: ToolbarProps) {
  return (
    <div className="toolbar">
      <div className="tool-group" role="group" aria-label="Tools">
        {tools.map(({ tool: t, label, Icon }) => (
          <button key={t} type="button" className="tool" aria-pressed={tool === t} onClick={() => onToolChange(t)}>
            <Icon />
            {label}
          </button>
        ))}
      </div>
      <span className="toolbar-divider" aria-hidden="true" />
      <div className="swatches" role="group" aria-label="New post-it colour">
        {noteColors.map((c) => (
          <button
            key={c}
            type="button"
            className={`swatch note-${c.toLowerCase()}`}
            title={c}
            aria-label={c}
            aria-pressed={c === color}
            onClick={() => onColorChange(c)}
          />
        ))}
      </div>
      <span className="toolbar-hint">{hints[tool]}</span>
    </div>
  )
}
