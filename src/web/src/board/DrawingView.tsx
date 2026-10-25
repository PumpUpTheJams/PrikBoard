import { useMemo, useState, type CSSProperties, type PointerEvent, type Ref } from 'react'
import { inkColors, type DrawingDetail, type DrawingStroke, type InkColor, type Point, type Rect, type StrokePoint } from '../api/types'
import { keepPress } from './gesture'
import { inkHex, MAX_SCALE, MIN_SCALE, reshapeStrokes, strokePaths, strokeSizes, useDrawingStrokes, type Reshape } from './drawings'
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
  // Worked out again only for other strokes (the loaded ones stay the same while the drawing does).
  const shapes = useMemo(() => strokes.map((stroke) => strokePaths(stroke.points, stroke.strokeWidth)), [strokes])
  return (
    <svg className="stroke" style={{ left: at.x, top: at.y }} width={1} height={1} aria-hidden="true">
      {strokes.map((stroke, i) => {
        const paths = shapes[i]
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
  onPointerDown: (e: PointerEvent<Element>) => void
  /** The canvas's zoom, to turn pointer travel into canvas pixels. */
  zoom?: number
  /**
   * Scales or turns the drawing, from its corner handles and the round handle above it while it is selected. Without
   * it the corners do nothing and there is no round handle. The preview stays until the promise settles, by when the
   * board shows the new strokes.
   */
  onReshape?: (reshape: Reshape) => Promise<unknown>
}

type Corner = 'TopLeft' | 'TopRight' | 'BottomLeft' | 'BottomRight'
const corners: Corner[] = ['TopLeft', 'TopRight', 'BottomLeft', 'BottomRight']

/**
 * Scaling from a corner (the opposite one stays put; it keeps its shape unless Shift is held) or turning with the handle
 * above (about the center, in steps of 15° with Shift). Points are on screen; `centre` is the box's center there.
 */
type ReshapeGesture = ({ kind: 'scale'; corner: Corner } | { kind: 'rotate'; centre: Point }) & {
  start: Point
  current: Point
  shift: boolean
  /** Set on release to the drawing's content URL: the preview stays until the saved drawing has a new one. */
  done?: string
}

const clampScale = (s: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, s))

/** What a reshape gesture does to a drawing at `rect`, on its canvas. */
function reshapeOf(g: ReshapeGesture, rect: Rect, zoom: number): Reshape {
  if (g.kind === 'rotate') {
    const angle = (p: Point) => Math.atan2(p.y - g.centre.y, p.x - g.centre.x)
    let degrees = ((angle(g.current) - angle(g.start)) * 180) / Math.PI
    degrees = ((((degrees + 180) % 360) + 360) % 360) - 180
    degrees = g.shift ? Math.round(degrees / 15) * 15 : Math.round(degrees * 10) / 10
    return { scaleX: 1, scaleY: 1, rotation: degrees, pivot: { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 } }
  }
  const left = g.corner.endsWith('Left')
  const top = g.corner.startsWith('Top')
  const dx = (g.current.x - g.start.x) / zoom
  const dy = (g.current.y - g.start.y) / zoom
  const w = Math.max(1, rect.width)
  const h = Math.max(1, rect.height)
  let scaleX = (w + (left ? -dx : dx)) / w
  let scaleY = (h + (top ? -dy : dy)) / h
  if (!g.shift) {
    // Along the box's diagonal, so a flat line scales by how far its long side is pulled.
    scaleX = scaleY = (scaleX * w * w + scaleY * h * h) / (w * w + h * h)
  }
  const tidy = (s: number) => Math.round(clampScale(s) * 1000) / 1000
  return {
    scaleX: tidy(scaleX),
    scaleY: tidy(scaleY),
    rotation: 0,
    pivot: { x: left ? rect.x + rect.width : rect.x, y: top ? rect.y + rect.height : rect.y },
  }
}

const unchanged = (r: Reshape) => r.scaleX === 1 && r.scaleY === 1 && r.rotation === 0

/** A drawing on a canvas; pressing any of its lines selects and moves all of it. Selected, its handles scale and turn it. */
export function DrawingView({ drawing, rect, selected, lifted, onPointerDown, zoom = 1, onReshape }: DrawingViewProps) {
  const loaded = useDrawingStrokes(drawing)
  const [held, setGesture] = useState<ReshapeGesture | null>(null)
  // Once the drawing has a new content URL, its saved strokes replace the preview.
  const gesture = held?.done && held.done !== drawing.contentUrl ? null : held
  const reshape = gesture && loaded ? reshapeOf(gesture, rect, zoom) : null
  const shaped = reshape && loaded && !unchanged(reshape) ? reshapeStrokes(rect, loaded, reshape) : null
  const strokes = shaped?.strokes ?? loaded
  const box: Rect = shaped ?? rect
  const classes = ['drawing']
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (gesture) classes.push('reshaping')

  function begin(e: PointerEvent<HTMLElement>, kind: 'rotate' | Corner) {
    if (e.button !== 0 || !loaded) return
    e.stopPropagation()
    e.preventDefault()
    e.currentTarget.setPointerCapture(e.pointerId)
    const start = { x: e.clientX, y: e.clientY }
    if (kind !== 'rotate') {
      setGesture({ kind: 'scale', corner: kind, start, current: start, shift: e.shiftKey })
      return
    }
    const r = e.currentTarget.closest('.drawing-box')!.getBoundingClientRect()
    setGesture({ kind: 'rotate', centre: { x: r.left + r.width / 2, y: r.top + r.height / 2 }, start, current: start, shift: e.shiftKey })
  }
  function move(e: PointerEvent<HTMLElement>) {
    if (!gesture || gesture.done) return
    e.stopPropagation()
    setGesture({ ...gesture, current: { x: e.clientX, y: e.clientY }, shift: e.shiftKey })
  }
  function end(e: PointerEvent<HTMLElement>) {
    if (!gesture || gesture.done) return
    e.stopPropagation()
    const released = { ...gesture, current: { x: e.clientX, y: e.clientY }, shift: e.shiftKey }
    const result = reshapeOf(released, rect, zoom)
    if (!onReshape || unchanged(result)) {
      setGesture(null)
      return
    }
    setGesture({ ...released, done: drawing.contentUrl })
    void onReshape(result).finally(() => setGesture(null))
  }
  const cancel = () => setGesture((g) => (g?.done ? g : null))
  const handlers = { onPointerMove: move, onPointerUp: end, onPointerCancel: cancel, onLostPointerCapture: cancel }

  return (
    <div
      className={classes.join(' ')}
      data-drawing={drawing.id}
      style={lifted ? ({ '--pivot': `${box.width / 2}px ${box.height / 2}px` } as CSSProperties) : undefined}
      onPointerDown={onPointerDown}
    >
      {strokes && <DrawingSvg strokes={strokes} colors={drawing.colors} at={box} hit={!gesture} />}
      {selected && (
        <div className="drawing-box" aria-hidden="true" style={{ left: box.x - 6, top: box.y - 6, width: box.width + 12, height: box.height + 12 }}>
          {corners.map((corner) =>
            onReshape ? (
              <span
                key={corner}
                className={`drawing-handle ${corner}`}
                title="Drag to resize (Shift: stretch)"
                onPointerDown={(e) => begin(e, corner)}
                {...handlers}
              />
            ) : (
              <span key={corner} className={`drawing-handle ${corner}`} />
            ),
          )}
          {onReshape && <span className="drawing-rotate" title="Drag to rotate (Shift: 15° steps)" onPointerDown={(e) => begin(e, 'rotate')} {...handlers} />}
          {gesture?.kind === 'rotate' && reshape && <span className="drawing-angle">{Math.round(reshape.rotation)}°</span>}
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

/** The drawing's ink, or null when its strokes have different inks. */
const inkOf = (drawing: DrawingDetail) => (drawing.colors.every((c) => c === drawing.colors[0]) ? drawing.colors[0] : null)

/** Under a selected drawing: who drew it, its ink (picking another recolors all its lines) and a delete button. */
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
