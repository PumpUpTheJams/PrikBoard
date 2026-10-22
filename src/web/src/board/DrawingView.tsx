import type { CSSProperties, PointerEvent, Ref } from 'react'
import { inkColors, type DrawingDetail, type DrawingStroke, type InkColor, type Point, type Rect, type StrokePoint } from '../api/types'
import { keepPress } from './gesture'
import { inkHex, strokePaths, strokeSizes, useDrawingStrokes } from './drawings'
import { TrashIcon } from './icons'

/** A stroke's line being drawn, at its place. */
export function StrokeSvg({ points, color, strokeWidth, at }: { points: StrokePoint[]; color: string; strokeWidth: number; at: Point }) {
  const paths = strokePaths(points, strokeWidth)
  return (
    <svg className="stroke" style={{ left: at.x, top: at.y }} width={1} height={1} aria-hidden="true">
      {paths.map((p, i) => (
        <path key={i} d={p.d} stroke={color} strokeWidth={p.width} />
      ))}
    </svg>
  )
}

/**
 * A drawing's strokes at its place, in one SVG, each in its own ink. With `hit`, a wider invisible path along each line
 * takes presses near it, so pressing any of its lines picks up the whole drawing.
 */
export function DrawingSvg({ strokes, colors, at, hit }: { strokes: DrawingStroke[]; colors: InkColor[]; at: Point; hit?: boolean }) {
  return (
    <svg className="stroke" style={{ left: at.x, top: at.y }} width={1} height={1} aria-hidden="true">
      {strokes.map((stroke, i) => {
        const paths = strokePaths(stroke.points, stroke.strokeWidth)
        const color = inkHex[colors[i] ?? colors.at(-1) ?? 'Chalk']
        return (
          <g key={i}>
            {hit && <path className="stroke-hit" d={paths.map((p) => p.d).join('')} strokeWidth={Math.max(14, stroke.strokeWidth * 2)} />}
            {paths.map((p, j) => (
              <path key={j} d={p.d} stroke={color} strokeWidth={p.width} />
            ))}
          </g>
        )
      })}
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

/** A drawing on a canvas; pressing any of its lines selects and moves all of it. */
export function DrawingView({ drawing, rect, selected, lifted, front, onPointerDown }: DrawingViewProps) {
  const strokes = useDrawingStrokes(drawing)
  const classes = ['drawing']
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (front) classes.push('in-front')
  return (
    <div className={classes.join(' ')} data-drawing={drawing.id} onPointerDown={onPointerDown}>
      {strokes && <DrawingSvg strokes={strokes} colors={drawing.colors} at={rect} hit />}
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

/** Its one ink, or null when its strokes have different ones. */
const inkOf = (drawing: DrawingDetail) => (drawing.colors.every((c) => c === drawing.colors[0]) ? drawing.colors[0] : null)

/** Under a selected drawing: who drew it, its ink (to change it, for all its lines at once) and a delete button. */
export function DrawingToolbar({ drawing, rect, zoom, onRecolor, onDelete }: DrawingToolbarProps) {
  return (
    <div
      className="drawing-toolbar"
      role="toolbar"
      aria-label="Drawing"
      style={{ left: rect.x, top: rect.y + rect.height + 18, transform: `scale(${1 / zoom})` }}
      onPointerDown={keepPress}
    >
      {/* Unnamed on a public board that hides its people. */}
      {drawing.addedBy && (
        <>
          <span className="drawing-author" title={`Drawn by ${drawing.addedBy}`}>
            drawn by {drawing.addedBy}
          </span>
          <span className="toolbar-divider" aria-hidden="true" />
        </>
      )}
      <InkSwatches value={inkOf(drawing)} onChange={onRecolor} />
      <span className="toolbar-divider" aria-hidden="true" />
      <button type="button" className="drawing-delete" aria-label="Delete drawing" title="Delete drawing (Del)" onClick={onDelete}>
        <TrashIcon size={15} />
      </button>
    </div>
  )
}

/** The twelve inks in two rows: light ones for the board, then dark ones for drawing over a card. */
export function InkSwatches({ value, onChange }: { value: InkColor | null; onChange: (color: InkColor) => void }) {
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

/** A drawing being dragged outside its canvas, at the pointer: `at` is its top-left on screen, `zoom` the scale to show it at. */
export function DrawingGhost({ drawing, at, zoom }: { drawing: DrawingDetail; at: Point; zoom: number }) {
  const strokes = useDrawingStrokes(drawing)
  return (
    <div className="drag-ghost drawing-ghost" aria-hidden="true" style={{ left: at.x, top: at.y, transform: `scale(${zoom})` }}>
      {strokes && <DrawingSvg strokes={strokes} colors={drawing.colors} at={{ x: 0, y: 0 }} />}
    </div>
  )
}

/** A board drawing dragged over a card's canvas, where it would land. */
export function IncomingDrawingView({ drawing, at }: { drawing: DrawingDetail; at: Point }) {
  const strokes = useDrawingStrokes(drawing)
  return strokes ? (
    <div className="incoming-drawing" aria-hidden="true">
      <DrawingSvg strokes={strokes} colors={drawing.colors} at={at} />
    </div>
  ) : null
}
