#!/usr/bin/env node
// Step 1 of moving a FigJam board to PrikBoard: downloads the file's node tree and its images through Figma's REST API
// into tools/figma-import/out, so convert.mjs can run as often as needed without calling Figma again. No dependencies.
//
//   FIGMA_TOKEN=figd_... node tools/figma-import/fetch.mjs --file=<file key or FigJam URL> [--out=tools/figma-import/out]
//
// The token is a Figma personal access token with the file_content:read scope (Figma → Settings → Security).
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { args, detectImage, eachNode, MAX_IMAGE_BYTES, safeId as safe } from './shared.mjs'

const token = process.env.FIGMA_TOKEN ?? args.token
if (!token) throw new Error('Set FIGMA_TOKEN (a Figma personal access token with file_content:read).')
if (!args.file) throw new Error('Give --file: the file key, or the FigJam URL (https://www.figma.com/board/<key>/...).')
const key = args.file.match(/figma\.com\/(?:board|file|design)\/([A-Za-z0-9]+)/)?.[1] ?? args.file
const out = args.out ?? 'tools/figma-import/out'
mkdirSync(join(out, 'images'), { recursive: true })
mkdirSync(join(out, 'renders'), { recursive: true })

async function figma(path) {
  for (let attempt = 1; ; attempt++) {
    const response = await fetch(`https://api.figma.com/v1${path}`, { headers: { 'X-Figma-Token': token } })
    if (response.status === 429 && attempt < 6) {
      const wait = Number(response.headers.get('retry-after') ?? 10)
      console.log(`Figma asks to slow down; waiting ${wait} s`)
      await new Promise((r) => setTimeout(r, wait * 1000))
      continue
    }
    if (!response.ok) throw new Error(`GET ${path}: ${response.status} ${await response.text()}`)
    return response.json()
  }
}

async function download(url) {
  const response = await fetch(url)
  if (!response.ok) throw new Error(`GET ${url}: ${response.status}`)
  return Buffer.from(await response.arrayBuffer())
}

console.log(`Reading file ${key}…`)
const file = await figma(`/files/${key}`)
writeFileSync(join(out, 'figma-file.json'), JSON.stringify(file))
console.log(`"${file.name}" (${file.editorType ?? 'unknown editor'}), saved to ${join(out, 'figma-file.json')}`)

// Every image fill in the file, with the nodes that show it.
const usedBy = new Map()
for (const page of file.document.children) {
  for (const node of eachNode(page)) {
    for (const fill of node.fills ?? []) {
      if (fill.type === 'IMAGE' && fill.imageRef && fill.visible !== false) {
        usedBy.set(fill.imageRef, [...(usedBy.get(fill.imageRef) ?? []), node.id])
      }
    }
  }
}

const { meta } = await figma(`/files/${key}/images`)
const urls = meta?.images ?? {}
const toRender = []
let downloaded = 0
for (const [ref, nodes] of usedBy) {
  const path = join(out, 'images', ref)
  if (!existsSync(path)) {
    if (!urls[ref]) {
      console.log(`No download for image ${ref}; rendering it instead`)
      toRender.push(...nodes)
      continue
    }
    writeFileSync(path, await download(urls[ref]))
    downloaded++
  }
  const bytes = readFileSync(path)
  // PrikBoard takes PNG, JPEG, GIF or WebP up to 10 MB; anything else is rendered as a JPEG by Figma.
  if (!detectImage(bytes) || bytes.length > MAX_IMAGE_BYTES) toRender.push(...nodes)
}

// Renders, in batches so the URLs stay short. Figma's render URLs expire, so the files are saved right away.
const missing = toRender.filter((id) => !existsSync(join(out, 'renders', `${safe(id)}.jpg`)))
for (let i = 0; i < missing.length; i += 50) {
  const ids = missing.slice(i, i + 50)
  const { images, err } = await figma(`/images/${key}?ids=${encodeURIComponent(ids.join(','))}&format=jpg&scale=1`)
  if (err) throw new Error(`Rendering failed: ${err}`)
  for (const id of ids) {
    if (images[id]) writeFileSync(join(out, 'renders', `${safe(id)}.jpg`), await download(images[id]))
  }
}

console.log(`${usedBy.size} images (${downloaded} downloaded now), ${toRender.length} rendered as JPEG. Next: node tools/figma-import/convert.mjs`)
