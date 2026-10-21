import type { CSSProperties, PointerEvent, Ref } from 'react'
import { inkColors, type DrawingDetail, type InkColor, type Point, type Rect, type StrokePoint } from '../api/types'
import { keepPress } from './gesture'
import { inkHex, strokePaths, strokeSizes, useDrawingPoints } from './drawings'
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
  /** Drawn above the rest, with the bucket that was opened or pressed last. */
  front?: boolean
  onPointerDown: (e: PointerEvent<Element>) => void
}

/** A pen stroke on the board; pressing its line selects and moves it. */
export function DrawingView({ drawing, rect, selected, lifted, front, onPointerDown }: DrawingViewProps) {
  const points = useDrawingPoints(drawing)
  const classes = ['drawing']
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (front) classes.push('in-front')
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

/** The twelve inks in two rows: light ones for the board, then dark ones for drawing over a card. */
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

/** The pen's flyout: its inks, then its three sizes. */
export function PenOptions({ ink, onInkChange, width, onWidthChange }: { ink: InkColor; onInkChange: (ink: InkColor) => void; width: number; onWidthChange: (width: number) => void }) {
  return (
    <>
      <InkSwatches value={ink} onChange={onInkChange} />
      <span className="toolbar-divider" aria-hidden="true" />
      {strokeSizes.map((s) => (
        <button
          key={s.width}
          type="button"
          className="pen-size"
          title={`${s.name} (${s.width} px)`}
          aria-label={`${s.name} (${s.width} px)`}
          aria-pressed={s.width === width}
          onClick={() => onWidthChange(s.width)}
        >
          <span style={{ width: s.dot, height: s.dot }} />
        </button>
      ))}
    </>
  )
}

/**
 * The ring under the pointer that shows the pen's size, in place of the cursor: as wide as the line at medium pressure,
 * plus a little room, at the canvas's zoom like the line itself. Hidden until `movePenRing` puts it somewhere.
 */
export function PenRing({ ringRef, width, zoom }: { ringRef: Ref<HTMLDivElement>; width: number; zoom: number }) {
  return (
    <div
      ref={ringRef}
      className="pen-ring"
      aria-hidden="true"
      style={{ display: 'none', '--ring': `${width + 8}px`, borderWidth: 1.5 / zoom } as CSSProperties}
    />
  )
}

/** A stroke being dragged outside its canvas, at the pointer: `at` is its top-left on screen, `zoom` the scale to show it at. */
export function DrawingGhost({ drawing, at, zoom }: { drawing: DrawingDetail; at: Point; zoom: number }) {
  const points = useDrawingPoints(drawing)
  return (
    <div className="drag-ghost drawing-ghost" aria-hidden="true" style={{ left: at.x, top: at.y, transform: `scale(${zoom})` }}>
      {points && <StrokeSvg points={points} color={inkHex[drawing.color]} strokeWidth={drawing.strokeWidth} at={{ x: 0, y: 0 }} />}
    </div>
  )
}

/** A board stroke dragged over a card's canvas, where it would land. */
export function IncomingDrawingView({ drawing, at }: { drawing: DrawingDetail; at: Point }) {
  const points = useDrawingPoints(drawing)
  return points ? (
    <div className="incoming-drawing" aria-hidden="true">
      <StrokeSvg points={points} color={inkHex[drawing.color]} strokeWidth={drawing.strokeWidth} at={at} />
    </div>
  ) : null
}
