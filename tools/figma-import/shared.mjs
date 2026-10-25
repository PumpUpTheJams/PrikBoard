// Helpers shared by fetch.mjs, convert.mjs and paste.mjs.
import { createHash } from 'node:crypto'

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

/** A node id as a file name (Figma ids look like 12:345). */
export function safeId(id) {
  return id.replace(/[^A-Za-z0-9-]/g, '_')
}

/** The same UUID every time for the same text, so runs on the same file give the same ids. */
export function uuidFrom(text) {
  const h = createHash('sha1').update(text).digest('hex')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-5${h.slice(13, 16)}-${((parseInt(h[16], 16) & 3) | 8).toString(16)}${h.slice(17, 20)}-${h.slice(20, 32)}`
}
