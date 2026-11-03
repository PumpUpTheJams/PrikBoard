#!/usr/bin/env node
// The last step for any source: pastes the parts a converter wrote onto a board, the way the web app
// pastes a big clip: each image is uploaded first (uploadPasteImage), then the part goes as one paste (pasteItems), so a
// part lands whole or not at all. Parts keep their places relative to each other. No dependencies.
//
//   PRIKBOARD_TOKEN=pb_pat_... node tools/board-import/paste.mjs --from=figma --board=<short id> [--server=http://localhost:5080]
//   PRIKBOARD_TOKEN=pb_pat_... node tools/board-import/paste.mjs --from=miro --name="From Miro"     (a new board)
//
// --board pastes onto a board you made in the web app (a token limited to that board, Editor, is enough). When that
// board holds nothing but its empty starting buckets (To do, Doing, Done), those are deleted first; otherwise the parts
// go to the right of what is there. --name makes a new board instead, which needs a token that isn't limited to boards.
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { args, detectImage } from './shared.mjs'

const token = process.env.PRIKBOARD_TOKEN ?? args.token
if (!token) throw new Error('Set PRIKBOARD_TOKEN (a PrikBoard personal access token, Settings → Access tokens).')
if (!args.board && !args.name) throw new Error('Give --board=<short id> (an existing board) or --name="…" (a new board).')
const server = (args.server ?? 'http://localhost:5080').replace(/\/+$/, '')
// --from=figma or --from=miro names the converter's folder; --out gives any other.
const out = args.out ?? (args.from ? `tools/board-import/out/${args.from}` : null)
if (!out) throw new Error('Give --from=figma or --from=miro (or --out=<folder with part-1.json>).')
const contentTypes = { png: 'image/png', jpg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp' }

async function call(method, path, body, type = 'application/json') {
  const response = await fetch(`${server}/api${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': type }) },
    body: body === undefined || type !== 'application/json' ? body : JSON.stringify(body),
  })
  const text = await response.text()
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text}`)
  return text ? JSON.parse(text) : undefined
}

const parts = []
for (let n = 1; existsSync(join(out, `part-${n}.json`)); n++) {
  parts.push({
    n,
    clip: JSON.parse(readFileSync(join(out, `part-${n}.json`), 'utf8')),
    files: JSON.parse(readFileSync(join(out, `part-${n}.files.json`), 'utf8')),
  })
}
if (parts.length === 0) throw new Error(`No ${join(out, 'part-1.json')}; run convert.mjs first.`)

const started = Date.now()
let board
if (args.board) {
  // getBoard by id, or getBoardByShortId.
  board = await call('GET', `/boards/${args.board}`)
} else {
  board = await call('POST', '/boards', { name: args.name })
  console.log(`Made board "${board.name}"`)
}

// Make room: an empty starting board loses its empty buckets; otherwise paste to the right of what is there.
let dx = 0
const empty = board.cards.length === 0 && board.images.length === 0 && (board.drawings ?? []).length === 0
if (empty && !args['keep-buckets']) {
  for (const bucket of board.buckets) await call('DELETE', `/buckets/${bucket.id}`)
  if (board.buckets.length > 0) console.log(`Deleted the empty buckets ${board.buckets.map((b) => `"${b.name}"`).join(', ')}`)
} else {
  const rights = [
    ...board.buckets.map((b) => b.x + b.width),
    ...board.cards.map((c) => c.x + 200),
    ...board.images.filter((i) => !i.cardId).map((i) => i.x + i.width),
    ...(board.drawings ?? []).filter((d) => !d.cardId).map((d) => d.x + (d.width ?? 0)),
  ]
  if (rights.length > 0) {
    const allParts = parts.flatMap(({ clip }) => corners(clip))
    dx = Math.max(...rights) + 200 - Math.min(...allParts.map((c) => c.x))
  }
}

function corners(clip) {
  return [...clip.cards, ...clip.buckets, ...clip.images.filter((i) => !i.cardId), ...clip.drawings.filter((d) => !d.cardId)]
}

for (const { n, clip, files } of parts) {
  // Card links between the part's own cards follow them; the source just has to be a valid one.
  clip.source = { url: server, boardShortId: 'FIGJAM', boardKey: 'FIG' }
  const items = corners(clip)
  const at = { x: Math.min(...items.map((c) => c.x)) + dx, y: Math.min(...items.map((c) => c.y)) }

  // Check before uploading anything, so a mistake shows up before minutes of uploads.
  await call('POST', `/boards/${board.id}/paste/check`, { clip, ...at, imageFiles: clip.images.map(() => null) })
  const imageFiles = []
  for (const [i, path] of files.entries()) {
    const bytes = readFileSync(path)
    const uploaded = await call('POST', `/boards/${board.id}/paste-images`, bytes, contentTypes[detectImage(bytes)] ?? 'application/octet-stream')
    imageFiles.push(uploaded.token)
    process.stdout.write(`\rPart ${n}: image ${i + 1} of ${files.length}`)
  }
  if (files.length > 0) process.stdout.write('\n')
  const pasted = await call('POST', `/boards/${board.id}/paste`, { clip, ...at, imageFiles })
  console.log(`Part ${n} of ${parts.length}: ${pasted.buckets.length} buckets, ${pasted.cards.length} cards, ${pasted.images.length} images`)
}

console.log(`Done in ${((Date.now() - started) / 1000).toFixed(1)} s: ${server}/#/boards/${board.shortId}`)
