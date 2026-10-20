// Pen strokes: building one from the pointer, the points' cache, and turning points into SVG paths.
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { DrawingDetail, InkColor, Point, StrokePoint } from '../api/types'
import { GRID, MAX_COORDINATE, MIN_COORDINATE } from './layout'

/** The inks' colours, as the `--ink-*` tokens in index.css. */
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

/** The most points a stroke can have (BoardDrawing.MaxPoints), and how far apart (canvas px) points need to be to count. */
const MAX_POINTS = 5000
const MIN_STEP = 1
/** Points of a drawing reach this far from its x/y. */
const MAX_OFFSET = 10_000

/** Pressure of a pointer event: a pen's own, 0.5 for a mouse or a touch (which report none, or only 0 and 1). */
export function pressureOf(e: { pointerType: string; pressure: number }) {
  return e.pointerType === 'pen' && e.pressure > 0 ? Math.min(1, e.pressure) : 0.5
}

/** Adds a point to a stroke being drawn (absolute canvas points), skipping ones too close to the last. */
export function addPoint(points: StrokePoint[], p: Point, pressure: number): StrokePoint[] {
  const last = points.at(-1)
  if (points.length >= MAX_POINTS) return points
  if (last && Math.hypot(p.x - last[0], p.y - last[1]) < MIN_STEP) {
    // Pressing harder on the spot still counts.
    if (pressure > last[2]) return [...points.slice(0, -1), [last[0], last[1], pressure]]
    return points
  }
  return [...points, [p.x, p.y, pressure]]
}

/**
 * A finished stroke as the API takes it: its top-left on the grid, at least half a (thickest) line clear of every point,
 * and the points measured from there. The server snaps x/y, so they are on the grid already and the line stays put.
 */
export function strokeToDrawing(points: StrokePoint[], strokeWidth: number) {
  const margin = maxWidth(strokeWidth) / 2
  const floor = (v: number) => Math.min(MAX_COORDINATE, Math.max(MIN_COORDINATE, Math.floor(v / GRID) * GRID))
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
 * SVG paths for a stroke. Pressure changes the width along the line, so runs of segments with about the same width
 * share a path (round caps and joins hide the seams); without pressure (a mouse) it is one path.
 */
export function strokePaths(points: StrokePoint[], base: number): { d: string; width: number }[] {
  if (points.length === 0) return []
  const n = (v: number) => Math.round(v * 10) / 10
  if (points.length === 1) {
    const [x, y, p] = points[0]
    return [{ d: `M${n(x)} ${n(y)}h0`, width: widthAt(base, p) }]
  }
  // Widths in steps of a quarter pixel.
  const quantize = (w: number) => Math.max(0.5, Math.round(w * 4) / 4)
  const paths: { d: string; width: number }[] = []
  let current: { d: string; width: number } | null = null
  for (let i = 1; i < points.length; i++) {
    const [ax, ay, ap] = points[i - 1]
    const [bx, by, bp] = points[i]
    const width = quantize(widthAt(base, (ap + bp) / 2))
    if (current && current.width === width) {
      current.d += `L${n(bx)} ${n(by)}`
    } else {
      current = { d: `M${n(ax)} ${n(ay)}L${n(bx)} ${n(by)}`, width }
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
