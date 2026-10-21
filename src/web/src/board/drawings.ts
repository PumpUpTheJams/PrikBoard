// Pen strokes: building one from the pointer, the points' cache, and turning points into SVG paths.
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { inkColors, type DrawingDetail, type InkColor, type Point, type StrokePoint } from '../api/types'
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

/** Points of drawings already fetched (or just drawn here), by id. They never change. */
const cache = new Map<string, StrokePoint[]>()
const pending = new Map<string, Promise<StrokePoint[]>>()

/** Remembers the points of a drawing just made, so it shows without fetching them. */
export function rememberPoints(id: string, points: StrokePoint[]) {
  cache.set(id, points)
}

function load(id: string) {
  let promise = pending.get(id)
  if (!promise) {
    promise = api.getDrawingContent(id).then((content) => {
      cache.set(id, content.points)
      return content.points
    })
    pending.set(id, promise)
    // A failed fetch can be tried again later.
    promise.catch(() => pending.delete(id))
  }
  return promise
}

/** A drawing's points: from the cache, or fetched once; null until they are there. */
export function useDrawingPoints(drawing: DrawingDetail): StrokePoint[] | null {
  const [loaded, setLoaded] = useState<{ id: string; points: StrokePoint[] } | null>(null)
  const cached = cache.get(drawing.id)
  useEffect(() => {
    if (cache.has(drawing.id)) return
    let live = true
    load(drawing.id).then(
      (points) => live && setLoaded({ id: drawing.id, points }),
      () => {},
    )
    return () => {
      live = false
    }
  }, [drawing.id])
  return cached ?? (loaded?.id === drawing.id ? loaded.points : null)
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
