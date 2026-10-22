// Pen strokes: building one from the pointer, the points' cache, and turning points into SVG paths.
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { inkColors, type DrawingDetail, type DrawingStroke, type InkColor, type Point, type StrokePoint } from '../api/types'
import { GRID, MAX_COORDINATE, MIN_COORDINATE } from './layout'

/** The inks' colors, as the `--ink-*` tokens in index.css. */
export const inkHex: Record<InkColor, string> = {
  Chalk: '#e6e8ec',
  Butter: '#f3d77a',
  Mint: '#9fe0c0',
  Sky: '#9ccbf5',
  Coral: '#f4a99a',
  Lilac: '#cdb8f5',
  Ink: '#17181b',
  Ochre: '#7a5a00',
  Pine: '#1f6249',
  Ocean: '#1d5a8c',
  Brick: '#963528',
  Violet: '#663d8f',
}

/** The pen's base widths, with the size of the dot that shows each in the flyout. */
export const strokeSizes = [
  { width: 2, name: 'Fine', dot: 4 },
  { width: 4, name: 'Medium', dot: 7 },
  { width: 8, name: 'Bold', dot: 12 },
] as const

/** Where the board's pen and the cards' pen keep their ink and size in this browser. */
export const BOARD_PEN = { key: 'prikboard.pen', ink: 'Chalk' } as const
/** The cards' pen keeps its own ink and size. The inside of an open card is dark like the board, so it starts light too. */
export const CARD_PEN = { key: 'prikboard.cardPen', ink: 'Chalk' } as const

function storedPen({ key, ink }: { key: string; ink: InkColor }): { ink: InkColor; width: number } {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? 'null') as { ink?: unknown; width?: unknown } | null
    return {
      ink: inkColors.find((c) => c === value?.ink) ?? ink,
      width: strokeSizes.find((s) => s.width === value?.width)?.width ?? 4,
    }
  } catch {
    return { ink, width: 4 }
  }
}

/** A pen's ink and size, as last picked in this browser (default: its own ink, medium), kept as they change. */
export function usePen(pen: { key: string; ink: InkColor }) {
  const [initial] = useState(() => storedPen(pen))
  const [ink, setInk] = useState<InkColor>(initial.ink)
  const [width, setWidth] = useState(initial.width)
  useEffect(() => {
    try {
      localStorage.setItem(pen.key, JSON.stringify({ ink, width }))
    } catch {
      // Storage can be unavailable (private windows, blocked site data); the pen then starts at the default.
    }
  }, [pen.key, ink, width])
  return { ink, setInk, width, setWidth }
}

/** The most points a stroke can have (BoardDrawing.MaxPoints), and how far apart (canvas px) points need to be to count. */
const MAX_POINTS = 5000
const MIN_STEP = 1
/** Points of a drawing reach this far from its x/y. */
const MAX_OFFSET = 10_000

/** Pressure of a pointer event: a pen's own, 0.5 for a mouse or a touch (which report none, or only 0 and 1). */
export function pressureOf(e: { pointerType: string; pressure: number }) {
  return e.pointerType === 'pen' && e.pressure > 0 ? Math.min(1, e.pressure) : 0.5
}

/** How much of a new pressure reading a point takes; the rest is the last point's, so a pen's jitter doesn't show. */
const PRESSURE_EASING = 0.35

/**
 * Adds a point to a stroke being drawn (absolute canvas points), skipping ones too close to the last. Pressure eases
 * towards the pen's readings rather than jumping with each one.
 */
export function addPoint(points: StrokePoint[], p: Point, pressure: number): StrokePoint[] {
  const last = points.at(-1)
  if (points.length >= MAX_POINTS) return points
  const eased = last ? last[2] + (pressure - last[2]) * PRESSURE_EASING : pressure
  if (last && Math.hypot(p.x - last[0], p.y - last[1]) < MIN_STEP) {
    // Pressing harder on the spot still counts.
    if (eased > last[2]) return [...points.slice(0, -1), [last[0], last[1], eased]]
    return points
  }
  return [...points, [p.x, p.y, eased]]
}

/**
 * A finished stroke as the API takes it: its top-left on the grid, at least half a (thickest) line clear of every point,
 * and the points measured from there. `min` is where the canvas starts: 0 on a card, where a line drawn right at
 * the edge is pushed in a little. The server snaps x/y, so they are on the grid already and the line stays put.
 */
export function strokeToDrawing(points: StrokePoint[], strokeWidth: number, min = MIN_COORDINATE) {
  const margin = maxWidth(strokeWidth) / 2
  const floor = (v: number) => Math.min(MAX_COORDINATE, Math.max(min, Math.floor(v / GRID) * GRID))
  const x = floor(Math.min(...points.map((p) => p[0])) - margin)
  const y = floor(Math.min(...points.map((p) => p[1])) - margin)
  const clamp = (v: number) => Math.min(MAX_OFFSET, Math.max(0, Math.round(v * 10) / 10))
  return {
    x,
    y,
    points: points.map(([px, py, pressure]): StrokePoint => [clamp(px - x), clamp(py - y), Math.round(pressure * 100) / 100]),
  }
}

/** A line's width at a pressure: the base width at 0.5, from 40% of it at no pressure to 160% at full. */
const widthAt = (base: number, pressure: number) => base * (0.4 + 1.2 * pressure)
const maxWidth = (base: number) => widthAt(base, 1)

/**
 * SVG paths for a stroke. The line runs through the middle of each pair of points, curving around the points themselves
 * (quadratic curves), so it reads smooth rather than as straight pieces. Pressure changes the width along it, so runs of
 * pieces with about the same width share a path (round caps and joins hide the seams); without pressure (a mouse) it is
 * one path.
 */
export function strokePaths(points: StrokePoint[], base: number): { d: string; width: number }[] {
  if (points.length === 0) return []
  const n = (v: number) => Math.round(v * 10) / 10
  const at = (x: number, y: number) => `${n(x)} ${n(y)}`
  if (points.length === 1) {
    const [x, y, p] = points[0]
    return [{ d: `M${at(x, y)}h0`, width: widthAt(base, p) }]
  }
  const mid = (i: number): [number, number] => [(points[i][0] + points[i + 1][0]) / 2, (points[i][1] + points[i + 1][1]) / 2]
  const last = points.length - 1
  // Pieces in drawing order: a line from the first point to the first middle, a curve around each point in between,
  // and a line from the last middle to the last point.
  const pieces: { from: [number, number] | StrokePoint; d: string; pressure: number }[] = []
  pieces.push({ from: points[0], d: `L${at(...mid(0))}`, pressure: (points[0][2] + points[1][2]) / 2 })
  for (let i = 1; i < last; i++) {
    const [cx, cy, pressure] = points[i]
    const [ex, ey] = mid(i)
    pieces.push({ from: mid(i - 1), d: `Q${at(cx, cy)} ${at(ex, ey)}`, pressure })
  }
  pieces.push({ from: mid(last - 1), d: `L${at(points[last][0], points[last][1])}`, pressure: (points[last - 1][2] + points[last][2]) / 2 })

  // Widths in steps of a quarter pixel.
  const quantize = (w: number) => Math.max(0.5, Math.round(w * 4) / 4)
  const paths: { d: string; width: number }[] = []
  let current: { d: string; width: number } | null = null
  for (const piece of pieces) {
    const width = quantize(widthAt(base, piece.pressure))
    if (current && current.width === width) {
      current.d += piece.d
    } else {
      current = { d: `M${at(piece.from[0], piece.from[1])}${piece.d}`, width }
      paths.push(current)
    }
  }
  return paths
}

/** A scale and turn about a pivot on the drawing's canvas, as transformDrawing takes it. */
export interface Reshape {
  scaleX: number
  scaleY: number
  /** Degrees clockwise, after scaling. */
  rotation: number
  pivot: Point
}

/** Scale factors transformDrawing takes. */
export const MIN_SCALE = 0.05
export const MAX_SCALE = 20

/**
 * A drawing's strokes scaled and turned about a pivot, with the box they then fill: BoardDrawing.Transform on the server,
 * so the preview shows where the lines will land. `at` is the drawing's corner on its canvas.
 */
export function reshapeStrokes(at: Point, strokes: DrawingStroke[], { scaleX, scaleY, rotation, pivot }: Reshape) {
  const radians = (rotation * Math.PI) / 180
  const cos = Math.cos(radians)
  const sin = Math.sin(radians)
  const moved = strokes.map((s) => ({
    strokeWidth: s.strokeWidth,
    points: s.points.map(([px, py, pressure]): StrokePoint => {
      const dx = (at.x + px - pivot.x) * scaleX
      const dy = (at.y + py - pivot.y) * scaleY
      return [pivot.x + dx * cos - dy * sin, pivot.y + dx * sin + dy * cos, pressure]
    }),
  }))
  // The corner: the last grid line half the thickest line (0.8 × the base width) clear of every point.
  const inset = (axis: 0 | 1) =>
    Math.min(...moved.map((s) => Math.floor((Math.min(...s.points.map((p) => p[axis])) - s.strokeWidth * 0.8) / GRID) * GRID))
  const x = inset(0)
  const y = inset(1)
  const round = (v: number) => Math.round(v * 10) / 10
  const shifted = moved.map((s) => ({ ...s, points: s.points.map(([px, py, p]): StrokePoint => [round(px - x), round(py - y), p]) }))
  const extent = (axis: 0 | 1) =>
    Math.max(...shifted.map((s) => Math.ceil(Math.max(...s.points.map((p) => p[axis]))) + Math.floor((s.strokeWidth + 1) / 2)))
  return { x, y, width: extent(0), height: extent(1), strokes: shifted }
}

/** A drawing's strokes if they are loaded already (they are while it shows), else null. */
export const loadedStrokes = (drawing: DrawingDetail): DrawingStroke[] | null => cache.get(drawing.contentUrl) ?? null

/** Strokes of drawings already fetched (or just drawn here), by content URL, which changes whenever they do. */
const cache = new Map<string, DrawingStroke[]>()
const pending = new Map<string, Promise<DrawingStroke[]>>()
/** What each drawing showed last, so a drawing that changed keeps showing until its new strokes are in. */
const shown = new Map<string, { x: number; y: number; strokes: DrawingStroke[] }>()

/** Remembers the strokes of a drawing just made, so it shows without fetching them. */
export function rememberStrokes(drawing: DrawingDetail, strokes: DrawingStroke[]) {
  cache.set(drawing.contentUrl, strokes)
}

/** A drawing's strokes: from the cache, or fetched once. */
export function loadDrawingStrokes(drawing: DrawingDetail): Promise<DrawingStroke[]> {
  const url = drawing.contentUrl
  const cached = cache.get(url)
  if (cached) return Promise.resolve(cached)
  let promise = pending.get(url)
  if (!promise) {
    promise = api.getDrawingContent(url).then((content) => {
      cache.set(url, content.strokes)
      return content.strokes
    })
    pending.set(url, promise)
    // A failed fetch can be tried again later.
    promise.catch(() => pending.delete(url))
  }
  return promise
}

/**
 * A drawing's strokes: from the cache, or fetched once; null until they are there. While a drawing's new strokes load,
 * its old ones stay on screen if it hasn't moved.
 */
export function useDrawingStrokes(drawing: DrawingDetail): DrawingStroke[] | null {
  const [loaded, setLoaded] = useState<{ url: string; strokes: DrawingStroke[] } | null>(null)
  const url = drawing.contentUrl
  const cached = cache.get(url)
  useEffect(() => {
    if (cache.has(url)) return
    let live = true
    loadDrawingStrokes(drawing).then(
      (strokes) => live && setLoaded({ url, strokes }),
      () => {},
    )
    return () => {
      live = false
    }
  }, [drawing, url])
  const strokes = cached ?? (loaded?.url === url ? loaded.strokes : null)
  const before = shown.get(drawing.id)
  if (strokes) {
    if (before?.strokes !== strokes) shown.set(drawing.id, { x: drawing.x, y: drawing.y, strokes })
    return strokes
  }
  return before && before.x === drawing.x && before.y === drawing.y ? before.strokes : null
}

/**
 * Puts a pen ring (PenRing) at a point on its canvas, or hides it: off the canvas, or for a finger, which covers it
 * anyway. It is moved directly rather than through state, so it keeps up with the pointer.
 */
export function movePenRing(ring: HTMLElement | null, at: Point | null, pointerType?: string) {
  if (!ring) return
  if (!at || pointerType === 'touch') {
    ring.style.display = 'none'
    return
  }
  ring.style.display = ''
  ring.style.transform = `translate(${at.x}px, ${at.y}px)`
}
