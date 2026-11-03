// Freehand drawings from other boards as PrikBoard strokes: SVG path data to points, source colors to PrikBoard inks.
import { colorName } from './shared.mjs'

// On the board (dark by default) the light inks show; `inks` in config.json picks others per color name.
const LIGHT_INKS = {
  yellow: 'Butter', orange: 'Coral', red: 'Coral', pink: 'Coral', violet: 'Lilac', blue: 'Sky', teal: 'Mint', green: 'Mint',
  gray: 'Chalk', white: 'Chalk', black: 'Chalk',
}
const INKS = new Set(['Chalk', 'Butter', 'Mint', 'Sky', 'Coral', 'Lilac', 'Ink', 'Ochre', 'Pine', 'Ocean', 'Brick', 'Violet'])

/** The PrikBoard ink for a color ({ r, g, b } from 0 to 1), and the color's name. */
export function inkFor(rgb, overrides = {}) {
  const name = colorName(rgb)
  const ink = overrides[name] ?? LIGHT_INKS[name]
  return { ink: INKS.has(ink) ? ink : 'Chalk', name }
}

/** The PrikBoard line width (2, 4 or 8) nearest to a width in pixels. */
export function snapWidth(width) {
  return width < 3 ? 2 : width < 6 ? 4 : 8
}

/**
 * The lines in SVG path data, each as a list of [x, y] points, with curves flattened into steps of about `step` pixels.
 * Each subpath (from an M) is one line; `closed` says whether it ended with Z.
 */
export function pathLines(d, step = 4) {
  const tokens = d.match(/[MmLlHhVvCcSsQqTtAaZz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?/g) ?? []
  const lines = []
  let line = null
  let i = 0
  let cmd = ''
  let x = 0
  let y = 0
  let startX = 0
  let startY = 0
  let lastControl = null
  const num = () => Number(tokens[i++])
  const hasNumber = () => i < tokens.length && !/[a-zA-Z]/.test(tokens[i])
  const add = (px, py) => line.points.push([px, py])
  const curve = (pts) => {
    // De Casteljau at evenly spaced t; the number of steps follows the control polygon's length.
    const length = pts.slice(1).reduce((n, p, k) => n + Math.hypot(p[0] - pts[k][0], p[1] - pts[k][1]), 0)
    const steps = Math.max(1, Math.min(200, Math.ceil(length / step)))
    for (let s = 1; s <= steps; s++) {
      const t = s / steps
      let level = pts
      while (level.length > 1) level = level.slice(1).map((p, k) => [level[k][0] + (p[0] - level[k][0]) * t, level[k][1] + (p[1] - level[k][1]) * t])
      add(level[0][0], level[0][1])
    }
  }
  while (i < tokens.length) {
    if (/[a-zA-Z]/.test(tokens[i])) cmd = tokens[i++]
    // A letter with no numbers after it (other than Z) is skipped; the next letter follows.
    if (!hasNumber() && cmd.toUpperCase() !== 'Z') continue
    if (!line && cmd.toUpperCase() !== 'M') {
      line = { points: [[x, y]], closed: false }
      lines.push(line)
    }
    const rel = cmd === cmd.toLowerCase()
    const ox = rel ? x : 0
    const oy = rel ? y : 0
    switch (cmd.toUpperCase()) {
      case 'M': {
        x = ox + num()
        y = oy + num()
        startX = x
        startY = y
        line = { points: [[x, y]], closed: false }
        lines.push(line)
        cmd = rel ? 'l' : 'L'
        lastControl = null
        break
      }
      case 'L':
        x = ox + num()
        y = oy + num()
        add(x, y)
        lastControl = null
        break
      case 'H':
        x = ox + num()
        add(x, y)
        lastControl = null
        break
      case 'V':
        y = oy + num()
        add(x, y)
        lastControl = null
        break
      case 'C': {
        const c1 = [ox + num(), oy + num()]
        const c2 = [ox + num(), oy + num()]
        const end = [ox + num(), oy + num()]
        curve([[x, y], c1, c2, end])
        lastControl = { kind: 'C', point: c2 }
        ;[x, y] = end
        break
      }
      case 'S': {
        const c1 = lastControl?.kind === 'C' ? [2 * x - lastControl.point[0], 2 * y - lastControl.point[1]] : [x, y]
        const c2 = [ox + num(), oy + num()]
        const end = [ox + num(), oy + num()]
        curve([[x, y], c1, c2, end])
        lastControl = { kind: 'C', point: c2 }
        ;[x, y] = end
        break
      }
      case 'Q': {
        const c = [ox + num(), oy + num()]
        const end = [ox + num(), oy + num()]
        curve([[x, y], c, end])
        lastControl = { kind: 'Q', point: c }
        ;[x, y] = end
        break
      }
      case 'T': {
        const c = lastControl?.kind === 'Q' ? [2 * x - lastControl.point[0], 2 * y - lastControl.point[1]] : [x, y]
        const end = [ox + num(), oy + num()]
        curve([[x, y], c, end])
        lastControl = { kind: 'Q', point: c }
        ;[x, y] = end
        break
      }
      case 'A': {
        // Arcs are rare in pen strokes: go straight to the end point.
        i += 5
        x = ox + num()
        y = oy + num()
        add(x, y)
        lastControl = null
        break
      }
      case 'Z':
        if (line) {
          line.closed = true
          add(startX, startY)
        }
        x = startX
        y = startY
        lastControl = null
        // Numbers right after Z (not valid SVG, but harmless) are read as lines rather than looping.
        cmd = rel ? 'l' : 'L'
        break
      default:
        i++
    }
  }
  return lines
}

/** Drops points less than a pixel from the last one kept, then keeps evenly spaced ones up to `max`. */
export function thinOut(points, max = 5000) {
  const kept = []
  for (const p of points) {
    const last = kept.at(-1)
    if (!last || Math.hypot(p[0] - last[0], p[1] - last[1]) >= 1) kept.push(p)
  }
  if (kept.length <= max) return kept
  const every = (kept.length - 1) / (max - 1)
  return Array.from({ length: max }, (_, k) => kept[Math.round(k * every)])
}
