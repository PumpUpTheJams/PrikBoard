// FigJam pen strokes: vector nodes with a line and no fill.
import { pathLines } from '../drawings.mjs'

/** A freehand stroke drawn with FigJam's pen or marker. */
export function isPen(node) {
  return (
    node.type === 'VECTOR' &&
    !(node.fills ?? []).some((f) => f.visible !== false) &&
    (node.strokes ?? []).some((s) => s.type === 'SOLID' && s.visible !== false)
  )
}

/**
 * The stroke's center lines in absolute coordinates, from the path data `?geometry=paths` adds, or null when Figma gave
 * only the outline of the stroke (closed shapes), which can't be drawn again as a pen line.
 */
export function penLines(node) {
  const box = node.absoluteBoundingBox
  const paths = [...(node.fillGeometry ?? []), ...(node.strokeGeometry ?? [])].map((g) => g.path).filter(Boolean)
  if (!box || paths.length === 0) return null
  // fillGeometry of an open path is the path itself; strokeGeometry is always the outline, so it comes last.
  for (const path of paths) {
    const lines = pathLines(path)
    if (lines.length > 0 && lines.every((l) => !l.closed)) {
      return lines.map((l) => l.points.map(([x, y]) => [box.x + x, box.y + y]))
    }
  }
  return null
}

/**
 * The stroke's center lines from Figma's SVG export of it, in absolute coordinates, with each path's color and width,
 * or null. The export writes a centered stroke as a path with a stroke attribute: the line itself, not its outline.
 * The SVG covers the stroke's full extent, centered on the node's box.
 */
export function svgLines(svg, node) {
  const box = node.absoluteBoundingBox
  const size = /<svg[^>]*\bwidth="([\d.]+)"[^>]*\bheight="([\d.]+)"/.exec(svg)
  if (!box || !size) return null
  const ox = box.x + box.width / 2 - Number(size[1]) / 2
  const oy = box.y + box.height / 2 - Number(size[2]) / 2
  const strokes = []
  for (const [tag] of svg.matchAll(/<path\b[^>]*>/g)) {
    const attr = (name) => new RegExp(`\\s${name}="([^"]*)"`).exec(tag)?.[1]
    const d = attr('d')
    const stroke = attr('stroke')
    if (!d || !stroke || stroke === 'none' || (attr('fill') && attr('fill') !== 'none')) continue
    const width = Number(attr('stroke-width') ?? node.strokeWeight ?? 4)
    for (const line of pathLines(d)) {
      strokes.push({ color: stroke, width, points: line.points.map(([x, y]) => [ox + x, oy + y]) })
    }
  }
  return strokes.length > 0 ? strokes : null
}

/** The stroke's ink color ({ r, g, b }) and width in pixels. */
export function penStyle(node) {
  const stroke = node.strokes.find((s) => s.type === 'SOLID' && s.visible !== false)
  return { color: stroke.color, width: node.strokeWeight ?? 4 }
}
