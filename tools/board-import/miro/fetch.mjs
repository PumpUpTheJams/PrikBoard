#!/usr/bin/env node
// Step 1 for a Miro board: downloads its items, connectors, tags and members, and the image files, through Miro's REST
// API (v2) into tools/board-import/out/miro, so convert.mjs can run as often as needed without calling Miro again. It
// also prints which kinds of items the board holds (and saves one of each to item-kinds.json), so it is clear what
// convert can bring over. No dependencies.
//
//   MIRO_TOKEN=... node tools/board-import/miro/fetch.mjs --board=<Miro board URL or id> [--out=tools/board-import/out/miro]
//
// The token is an access token of a Miro app with the boards:read scope, installed to your team (Miro → Profile →
// Settings → Your apps → Create new app).
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { args, detectImage, MAX_IMAGE_BYTES } from '../shared.mjs'

const token = process.env.MIRO_TOKEN ?? args.token
if (!token) throw new Error('Set MIRO_TOKEN (an access token of a Miro app with boards:read).')
if (!args.board) throw new Error('Give --board: the Miro board URL (https://miro.com/app/board/<id>=/) or its id.')
const boardId = decodeURIComponent(args.board.match(/miro\.com\/app\/board\/([^/?#]+)/)?.[1] ?? args.board).replace(/\/$/, '')
const out = args.out ?? 'tools/board-import/out/miro'
mkdirSync(join(out, 'images'), { recursive: true })

async function miro(url, { json = true } = {}) {
  const full = url.startsWith('http') ? url : `https://api.miro.com/v2${url}`
  for (let attempt = 1; ; attempt++) {
    const response = await fetch(full, { headers: { Authorization: `Bearer ${token}`, Accept: json ? 'application/json' : '*/*' } })
    if (response.status === 429 && attempt < 8) {
      const reset = Number(response.headers.get('x-ratelimit-reset'))
      const wait = Number(response.headers.get('retry-after')) || (reset ? Math.max(1, reset - Date.now() / 1000) : 10)
      console.log(`Miro asks to slow down; waiting ${Math.ceil(wait)} s`)
      await new Promise((r) => setTimeout(r, Math.ceil(wait) * 1000))
      continue
    }
    if (!response.ok) throw new Error(`GET ${full}: ${response.status} ${await response.text()}`)
    return json ? response.json() : response
  }
}

/** Every page of a list: cursor lists (items, connectors) and offset lists (tags, members). */
async function all(path) {
  const results = []
  let cursor = null
  let offset = 0
  for (;;) {
    const sep = path.includes('?') ? '&' : '?'
    const page = await miro(`${path}${sep}limit=50${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}${offset ? `&offset=${offset}` : ''}`)
    const data = page.data ?? []
    results.push(...data)
    if (page.cursor) cursor = page.cursor
    else if (typeof page.total === 'number' && results.length < page.total && data.length > 0) offset = results.length
    else break
  }
  return results
}

const board = await miro(`/boards/${encodeURIComponent(boardId)}`)
console.log(`Reading "${board.name}"…`)
const items = await all(`/boards/${encodeURIComponent(boardId)}/items`)
const connectors = await all(`/boards/${encodeURIComponent(boardId)}/connectors`)
const tags = await all(`/boards/${encodeURIComponent(boardId)}/tags`).catch((e) => (console.log(`No tags: ${e.message}`), []))
const members = await all(`/boards/${encodeURIComponent(boardId)}/members`).catch((e) => (console.log(`No members: ${e.message}`), []))

// Tags of sticky notes and cards: in the item itself when Miro sends them there, else asked for one item at a time.
const tagged = items.filter((i) => i.type === 'sticky_note' || i.type === 'card')
const itemTags = {}
if (tags.length > 0 && tagged.length > 0 && !tagged.some((i) => Array.isArray(i.tagIds) || Array.isArray(i.data?.tagIds))) {
  console.log(`Reading the tags of ${tagged.length} sticky notes and cards…`)
  for (const item of tagged) {
    const answer = await miro(`/boards/${encodeURIComponent(boardId)}/items/${item.id}/tags`)
    itemTags[item.id] = (answer.tags ?? answer.data ?? []).map((t) => t.id)
  }
}

writeFileSync(join(out, 'miro-board.json'), JSON.stringify({ board, items, connectors, tags, members, itemTags }))

// What kinds of items there are, with one of each saved, so it is clear what can come over (drawings in particular).
const kinds = new Map()
for (const item of items) kinds.set(item.type, [...(kinds.get(item.type) ?? []), item])
writeFileSync(join(out, 'item-kinds.json'), JSON.stringify(Object.fromEntries([...kinds].map(([k, v]) => [k, v[0]])), null, 1))
console.log(`${items.length} items: ${[...kinds].map(([k, v]) => `${v.length} ${k}`).join(', ')}`)
console.log(`${connectors.length} connectors, ${tags.length} tags, ${members.length} members`)

// Image files: the original when Miro has one PrikBoard can take, else its preview.
let downloaded = 0
const failed = []
for (const item of kinds.get('image') ?? []) {
  const path = join(out, 'images', item.id)
  if (existsSync(path)) continue
  const bytes = (await imageFile(item, 'original')) ?? (await imageFile(item, 'preview'))
  if (bytes) {
    writeFileSync(path, bytes)
    downloaded++
  } else {
    failed.push(item.id)
  }
}

async function imageFile(item, format) {
  const source = item.data?.imageUrl
  if (!source) return null
  const url = new URL(source)
  url.searchParams.set('format', format)
  url.searchParams.set('redirect', 'false')
  try {
    const response = await miro(url.toString(), { json: false })
    let bytes = Buffer.from(await response.arrayBuffer())
    // With redirect=false Miro answers with a short-lived download link rather than the file.
    if ((response.headers.get('content-type') ?? '').includes('json')) {
      const link = JSON.parse(bytes.toString('utf8')).url
      if (!link) return null
      const file = await fetch(link)
      if (!file.ok) return null
      bytes = Buffer.from(await file.arrayBuffer())
    }
    return detectImage(bytes) && bytes.length <= MAX_IMAGE_BYTES ? bytes : null
  } catch (e) {
    console.log(`Image ${item.id} (${format}): ${e.message}`)
    return null
  }
}

console.log(`${(kinds.get('image') ?? []).length} images (${downloaded} downloaded now${failed.length ? `, ${failed.length} without a file PrikBoard can take` : ''}).`)
console.log('Next: node tools/board-import/miro/convert.mjs')
