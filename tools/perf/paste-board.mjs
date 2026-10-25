#!/usr/bin/env node
// Pastes a clip (from make-board.mjs, say) into a new board on a PrikBoard server, the way the web app pastes a big
// one: each image is uploaded first (uploadPasteImage), then the clip goes without the images' data (pasteItems). It
// needs a personal access token that can write and create boards (Settings → Access tokens). No dependencies.
//
//   node tools/perf/paste-board.mjs --clip=tools/perf/out/perf-board.json --token=pb_pat_... [--server=http://localhost:5080] [--name="Perf"]
import { readFileSync } from 'node:fs'

const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [key, ...value] = a.replace(/^--/, '').split('=')
    return [key, value.join('=') || 'true']
  }),
)
if (!args.clip || !args.token) throw new Error('Give --clip (a clip file) and --token (a personal access token).')
const server = (args.server ?? 'http://localhost:5080').replace(/\/+$/, '')
const clip = JSON.parse(readFileSync(args.clip, 'utf8'))

async function call(method, path, body, type = 'application/json') {
  const response = await fetch(`${server}/api${path}`, {
    method,
    headers: { Authorization: `Bearer ${args.token}`, 'Content-Type': type },
    body: body === undefined || type !== 'application/json' ? body : JSON.stringify(body),
  })
  const text = await response.text()
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text}`)
  return text ? JSON.parse(text) : undefined
}

const started = Date.now()
const board = await call('POST', '/boards', { name: args.name ?? `Perf ${clip.cards.length} cards` })
const imageFiles = []
for (const image of clip.images) {
  const uploaded = image.data ? await call('POST', `/boards/${board.id}/paste-images`, Buffer.from(image.data, 'base64'), 'application/octet-stream') : null
  imageFiles.push(uploaded?.token ?? null)
  image.data = ''
}
// The clip keeps its layout, with its top-left at the same place.
const corners = [...clip.cards, ...(clip.buckets ?? []), ...clip.images.filter((i) => !i.cardId), ...clip.drawings.filter((d) => !d.cardId)]
const x = Math.min(...corners.map((c) => c.x))
const y = Math.min(...corners.map((c) => c.y))
const pasted = await call('POST', `/boards/${board.id}/paste`, { clip, x, y, imageFiles })
console.log(
  `Pasted ${pasted.cards.length} cards, ${pasted.buckets.length} buckets, ${pasted.images.length} images and ${pasted.drawings.length} drawings ` +
    `into "${board.name}" (${server}/#/boards/${board.shortId}) in ${((Date.now() - started) / 1000).toFixed(1)} s`,
)
