import type { PointerEvent } from 'react'
import { inkColors, type DrawingDetail, type InkColor, type Point, type Rect, type StrokePoint } from '../api/types'
import { keepPress } from './gesture'
import { inkHex, strokePaths, useDrawingPoints } from './drawings'
import { TrashIcon } from './icons'

/** A stroke's line, at its place: the parts of the path, plus a wider invisible one that takes presses near the line. */
export function StrokeSvg({ points, color, strokeWidth, at, hit }: { points: StrokePoint[]; color: string; strokeWidth: number; at: Point; hit?: boolean }) {
  const paths = strokePaths(points, strokeWidth)
  return (
    <svg className="stroke" style={{ left: at.x, top: at.y }} width={1} height={1} aria-hidden="true">
      {hit && <path className="stroke-hit" d={paths.map((p) => p.d).join('')} strokeWidth={Math.max(14, strokeWidth * 2)} />}
      {paths.map((p, i) => (
        <path key={i} d={p.d} stroke={color} strokeWidth={p.width} />
      ))}
    </svg>
  )
}

interface DrawingViewProps {
  drawing: DrawingDetail
  /** Where to draw it: the server's place, or the live one while it moves. */
  rect: Rect
  selected: boolean
  lifted: boolean
  onPointerDown: (e: PointerEvent<Element>) => void
}

/** A pen stroke on the board; pressing its line selects and moves it. */
export function DrawingView({ drawing, rect, selected, lifted, onPointerDown }: DrawingViewProps) {
  const points = useDrawingPoints(drawing)
  const classes = ['drawing']
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  return (
    <div className={classes.join(' ')} data-drawing={drawing.id} onPointerDown={onPointerDown}>
      {points && <StrokeSvg points={points} color={inkHex[drawing.color]} strokeWidth={drawing.strokeWidth} at={rect} hit />}
      {selected && (
        <div className="drawing-box" aria-hidden="true" style={{ left: rect.x - 6, top: rect.y - 6, width: rect.width + 12, height: rect.height + 12 }}>
          <span className="drawing-handle" style={{ left: -4, top: -4 }} />
          <span className="drawing-handle" style={{ right: -4, top: -4 }} />
          <span className="drawing-handle" style={{ left: -4, bottom: -4 }} />
          <span className="drawing-handle" style={{ right: -4, bottom: -4 }} />
        </div>
      )}
    </div>
  )
}

interface DrawingToolbarProps {
  drawing: DrawingDetail
  /** Where the drawing is now, on the canvas. */
  rect: Rect
  /** The board's zoom: the toolbar keeps its size on screen. */
  zoom: number
  onRecolor: (color: InkColor) => void
  onDelete: () => void
}

/** Under a selected drawing: its ink, to change it, and a delete button. */
export function DrawingToolbar({ drawing, rect, zoom, onRecolor, onDelete }: DrawingToolbarProps) {
  return (
    <div
      className="drawing-toolbar"
      role="toolbar"
      aria-label="Drawing"
      style={{ left: rect.x, top: rect.y + rect.height + 18, transform: `scale(${1 / zoom})` }}
      onPointerDown={keepPress}
    >
      <InkSwatches value={drawing.color} onChange={onRecolor} />
      <span className="toolbar-divider" aria-hidden="true" />
      <button type="button" className="drawing-delete" aria-label="Delete drawing" title="Delete drawing (Del)" onClick={onDelete}>
        <TrashIcon size={15} />
      </button>
    </div>
  )
}

/** The twelve inks in two rows: light ones for the board, then dark ones for drawing over a post-it. */
export function InkSwatches({ value, onChange }: { value: InkColor; onChange: (color: InkColor) => void }) {
  return (
    <div className="ink-swatches">
      {inkColors.map((c) => (
        <button
          key={c}
          type="button"
          className={`swatch ink-${c.toLowerCase()}`}
          title={c}
          aria-label={c}
          aria-pressed={c === value}
          onClick={() => onChange(c)}
        />
      ))}
    </div>
  )
}
