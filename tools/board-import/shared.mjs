// Helpers shared by fetch.mjs, convert.mjs and paste.mjs.
import { createHash } from 'node:crypto'
import { existsSync, readFileSync } from 'node:fs'

/** Command-line options as `--name=value` (or `--flag` for true). */
export const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [key, ...value] = a.replace(/^--/, '').split('=')
    return [key, value.join('=') || 'true']
  }),
)

/** PrikBoard takes image files up to 10 MB. */
export const MAX_IMAGE_BYTES = 10 * 1024 * 1024

/** Every visible node under (and including) a node, depth first, with its ancestors. */
export function* eachNode(node, ancestors = []) {
  if (node.visible === false) return
  yield Object.assign(node, { ancestors })
  for (const child of node.children ?? []) yield* eachNode(child, [...ancestors, node])
}

/** The format PrikBoard will read from the bytes (PNG, JPEG, GIF or WebP), or null. */
export function detectImage(bytes) {
  if (bytes.length < 12) return null
  if (bytes[0] === 0x89 && bytes.toString('latin1', 1, 4) === 'PNG') return 'png'
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return 'jpg'
  if (bytes.toString('latin1', 0, 4) === 'GIF8') return 'gif'
  if (bytes.toString('latin1', 0, 4) === 'RIFF' && bytes.toString('latin1', 8, 12) === 'WEBP') return 'webp'
  return null
}

/** A color ({ r, g, b } from 0 to 1) by name: red, orange, yellow, green, teal, blue, violet, pink, white, gray or black. */
export function colorName({ r, g, b }) {
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  const s = max === min ? 0 : (max - min) / (1 - Math.abs(2 * l - 1))
  if (s < 0.15) return l > 0.9 ? 'white' : l < 0.25 ? 'black' : 'gray'
  const d = max - min
  let h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4
  h = (h * 60 + 360) % 360
  if (h < 15 || h >= 345) return 'red'
  if (h < 45) return 'orange'
  if (h < 70) return 'yellow'
  if (h < 165) return 'green'
  if (h < 195) return 'teal'
  if (h < 255) return 'blue'
  if (h < 290) return 'violet'
  return 'pink'
}

/** A color written as #rrggbb, as { r, g, b } from 0 to 1. */
export function hexColor(hex) {
  const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})/i.exec(hex ?? '')
  return m ? { r: parseInt(m[1], 16) / 255, g: parseInt(m[2], 16) / 255, b: parseInt(m[3], 16) / 255 } : null
}

/** The settings in config.json (or --config), with a source's own section merged over the shared ones. */
export function loadConfig(source, defaults) {
  const path = args.config ?? 'tools/board-import/config.json'
  const all = existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : {}
  const { figma, miro, ...shared } = all
  return { ...defaults, ...shared, ...({ figma, miro }[source] ?? {}) }
}

/** A node id as a file name (Figma ids look like 12:345). */
export function safeId(id) {
  return id.replace(/[^A-Za-z0-9-]/g, '_')
}

/** The same UUID every time for the same text, so runs on the same file give the same ids. */
export function uuidFrom(text) {
  const h = createHash('sha1').update(text).digest('hex')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-5${h.slice(13, 16)}-${((parseInt(h[16], 16) & 3) | 8).toString(16)}${h.slice(17, 20)}-${h.slice(20, 32)}`
}
