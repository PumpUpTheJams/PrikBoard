#!/usr/bin/env node
// Step 2 of moving a FigJam board to PrikBoard: turns what fetch.mjs saved into PrikBoard clips (the format pasteItems
// takes) and a report. Sections (and frames) become buckets, stickies become cards, images come along, connectors between
// stickies become links. Shapes with text, loose text and everything else are left out and counted in the report. No
// dependencies, and it doesn't call Figma.
//
//   node tools/figma-import/convert.mjs [--out=tools/figma-import/out] [--config=tools/figma-import/config.json]
//
// It writes out/part-1.json (and part-2.json, … when the board is too big for one paste), each with a part-N.files.json
// listing the image file of every image in it, and out/report.md: the mapping from Figma nodes to buckets and cards.
import { existsSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { args, detectImage, eachNode, MAX_IMAGE_BYTES, safeId, uuidFrom } from './shared.mjs'

const out = args.out ?? 'tools/figma-import/out'
const configPath = args.config ?? 'tools/figma-import/config.json'
const config = {
  page: null,
  scale: 0.85,
  framesAsBuckets: true,
  attachImagesToCards: true,
  includeAuthor: true,
  titleLength: 80,
  colorTags: {},
  colors: {},
  ...(existsSync(configPath) ? JSON.parse(readFileSync(configPath, 'utf8')) : {}),
}

// PrikBoard's limits (src/PrikBoard.Api: Layout, BoardClip, CardComponent, CardTags, BoardTag).
const CARD_WIDTH = 200
const CARD_HEIGHT = 120
const MIN_BUCKET_WIDTH = 240
const MIN_BUCKET_HEIGHT = 160
const MAX_BUCKET_SIZE = 10_000
const MIN_IMAGE_WIDTH = 80
const MAX_IMAGE_WIDTH = 4000
const MAX_COORDINATE = 100_000
const MAX_TITLE = 200
const MAX_TEXT = 4000
const MAX_COMPONENTS = 30
const MAX_LINKS = 20
const MAX_TAGS_PER_CARD = 10
const MAX_TAG_LENGTH = 30
const MAX_BOARD_TAGS = 100
const LIMITS = { cards: 2000, images: 200, buckets: 100, imageBytes: 150 * 1024 * 1024 }
const CLIP_VERSION = 6
// Card links in a clip name cards by id under some board id; the paste points them at the new cards.
const CLIP_BOARD_ID = uuidFrom('figjam-import-board')

const fills = {
  yellow: 'Butter', orange: 'Coral', red: 'Coral', pink: 'Coral', violet: 'Lilac', blue: 'Sky', teal: 'Mint', green: 'Mint',
  gray: 'Butter', white: 'Butter', black: 'Butter', ...config.colors,
}

const file = JSON.parse(readFileSync(join(out, 'figma-file.json'), 'utf8'))
const pages = file.document.children
const page = config.page == null ? pages[0] : pages.find((p) => p.name === config.page) ?? pages[Number(config.page)]
if (!page) throw new Error(`No page "${config.page}". Pages: ${pages.map((p) => p.name).join(', ')}`)

// ---- Read the tree ----------------------------------------------------------------------------------------------
const sections = []
const stickies = []
const imageNodes = []
const connectors = []
const skipped = new Map()
const skip = (why) => skipped.set(why, (skipped.get(why) ?? 0) + 1)
const isSection = (n) => n.type === 'SECTION' || (config.framesAsBuckets && n.type === 'FRAME')
const containers = new Set(['CANVAS', 'GROUP', 'SECTION', 'FRAME', 'COMPONENT', 'COMPONENT_SET', 'INSTANCE', 'BOOLEAN_OPERATION'])

for (const node of eachNode(page)) {
  if (node === page) continue
  const section = node.ancestors.findLast(isSection)?.id ?? null
  const box = node.absoluteBoundingBox
  const imageFill = (node.fills ?? []).find((f) => f.type === 'IMAGE' && f.imageRef && f.visible !== false)
  if (isSection(node) && box) {
    sections.push({ node, box, parent: section, collapsed: node.sectionContentsHidden === true })
  } else if (node.type === 'STICKY' && box) {
    stickies.push({ node, box, section })
  } else if (node.type === 'CONNECTOR') {
    connectors.push(node)
  } else if (imageFill && box) {
    imageNodes.push({ node, box, section, ref: imageFill.imageRef })
  } else if (node.type === 'SHAPE_WITH_TEXT') {
    skip('shapes with text')
  } else if (node.type === 'TEXT') {
    if (!node.ancestors.some((a) => a.type === 'STICKY')) skip('loose text')
  } else if (!containers.has(node.type)) {
    skip(`${node.type.toLowerCase().replaceAll('_', ' ')} nodes`)
  }
}

// ---- Coordinates ------------------------------------------------------------------------------------------------
const boxes = [...sections, ...stickies, ...imageNodes].map((i) => i.box)
if (boxes.length === 0) throw new Error(`Nothing to move on page "${page.name}": no sections, stickies or images.`)
const minX = Math.min(...boxes.map((b) => b.x))
const minY = Math.min(...boxes.map((b) => b.y))
const at = (v, min) => Math.round((v - min) * config.scale)
const px = (v) => Math.round(v * config.scale)
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v))
const centerOf = (b) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 })
const inside = (p, b) => p.x >= b.x && p.x <= b.x + b.width && p.y >= b.y && p.y <= b.y + b.height

// ---- Buckets (outer sections before the ones inside them, so those stack on top) --------------------------------
const bucketOf = new Map()
const buckets = sections.map(({ node, box, parent, collapsed }) => {
  const bucket = {
    id: uuidFrom(`bucket:${node.id}`),
    name: (node.name?.trim() || 'Section').slice(0, 100),
    x: clamp(at(box.x, minX), -MAX_COORDINATE, MAX_COORDINATE),
    y: clamp(at(box.y, minY), -MAX_COORDINATE, MAX_COORDINATE),
    width: clamp(px(box.width), MIN_BUCKET_WIDTH, MAX_BUCKET_SIZE),
    height: clamp(px(box.height), MIN_BUCKET_HEIGHT, MAX_BUCKET_SIZE),
    collapsed,
  }
  bucketOf.set(node.id, bucket)
  return { bucket, node, parent }
})

// ---- Cards ------------------------------------------------------------------------------------------------------
const allTags = new Set()
const warnings = []
const normalizeTag = (t) => t.trim().toLowerCase().replace(/\s+/g, '-').slice(0, MAX_TAG_LENGTH)

function colorName(node) {
  const fill = (node.fills ?? []).find((f) => f.type === 'SOLID' && f.visible !== false)
  if (!fill) return 'yellow'
  const { r, g, b } = fill.color
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

function split(text, size) {
  const parts = []
  let rest = text
  while (rest.length > size) {
    const cut = Math.max(rest.lastIndexOf('\n', size), rest.lastIndexOf(' ', size), size / 2)
    parts.push(rest.slice(0, cut).trimEnd())
    rest = rest.slice(cut).trimStart()
  }
  return [...parts, rest].filter((p) => p.length > 0)
}

function shorten(line, size) {
  if (line.length <= size) return line
  const cut = line.lastIndexOf(' ', size - 1)
  return `${line.slice(0, cut > size / 2 ? cut : size - 1).trimEnd()}…`
}

const cardOf = new Map()
const cards = stickies.map(({ node, box, section }, index) => {
  const color = colorName(node)
  const raw = (node.characters ?? '').replace(/\r\n?/g, '\n').trim()
  const hashtags = [...raw.matchAll(/(^|\s)#([\p{L}\p{N}][\p{L}\p{N}_-]*)/gu)].map((m) => m[2])
  const text = raw.replace(/(^|[ \t])#[\p{L}\p{N}][\p{L}\p{N}_-]*/gu, '$1').replace(/[ \t]+$/gm, '').replace(/\n{3,}/g, '\n\n').trim()
  const lines = text.split('\n').map((l) => l.trim()).filter(Boolean)
  const title = shorten(lines[0] ?? '', Math.min(MAX_TITLE, config.titleLength))
  let body = lines.length > 1 || title !== (lines[0] ?? '') ? text : ''
  if (config.includeAuthor && node.authorVisible !== false && node.authorName) {
    body = `${body}${body ? '\n\n' : ''}From FigJam, by ${node.authorName}`
  }
  const tags = [...new Set([...hashtags, config.colorTags[color]].filter(Boolean).map(normalizeTag).filter(Boolean))]
  if (tags.length > MAX_TAGS_PER_CARD) warnings.push(`"${title}" has ${tags.length} tags; kept the first ${MAX_TAGS_PER_CARD}.`)
  const kept = tags.slice(0, MAX_TAGS_PER_CARD)
  kept.forEach((t) => allTags.add(t))
  const c = centerOf(box)
  const card = {
    id: uuidFrom(`card:${node.id}`),
    number: index + 1,
    title,
    color: fills[color] ?? 'Butter',
    tags: kept,
    x: clamp(at(c.x, minX) - CARD_WIDTH / 2, -MAX_COORDINATE, MAX_COORDINATE),
    y: clamp(at(c.y, minY) - CARD_HEIGHT / 2, -MAX_COORDINATE, MAX_COORDINATE),
    components: [],
    bucketId: section ? bucketOf.get(section).id : null,
  }
  let y = 20
  for (const chunk of split(body, MAX_TEXT)) {
    // About 50 characters a line at 400 px wide, 20 px a line.
    const rows = chunk.split('\n').reduce((n, line) => n + Math.max(1, Math.ceil(line.length / 50)), 0)
    const height = clamp(rows * 20 + 60, 80, 2000)
    card.components.push({ kind: 'Text', text: chunk, x: 20, y, width: 400, height })
    y += height + 20
  }
  const item = { card, node, box, color, section, images: [], links: [] }
  cardOf.set(node.id, item)
  return item
})

// ---- Connectors between stickies become links on the card they start from -----------------------------------------
let droppedConnectors = 0
for (const connector of connectors) {
  const from = cardOf.get(connector.connectorStart?.endpointNodeId)
  const to = cardOf.get(connector.connectorEnd?.endpointNodeId)
  if (!from || !to || from === to) {
    droppedConnectors++
    continue
  }
  if (!from.links.includes(to)) from.links.push(to)
}

// ---- Images -----------------------------------------------------------------------------------------------------
const images = []
for (const { node, box, section, ref } of imageNodes) {
  let path = join(out, 'images', ref)
  let bytes = existsSync(path) ? readFileSync(path) : null
  if (!bytes || !detectImage(bytes) || bytes.length > MAX_IMAGE_BYTES) {
    path = join(out, 'renders', `${safeId(node.id)}.jpg`)
    bytes = existsSync(path) ? readFileSync(path) : null
  }
  if (!bytes || !detectImage(bytes) || bytes.length > MAX_IMAGE_BYTES) {
    warnings.push(`Image "${node.name}" (${node.id}) left out: no file PrikBoard can take${bytes ? ` (${(bytes.length / 1e6).toFixed(1)} MB)` : ''}. Run fetch.mjs again?`)
    continue
  }
  const width = clamp(px(box.width), MIN_IMAGE_WIDTH, MAX_IMAGE_WIDTH)
  const height = Math.max(1, Math.round((width * box.height) / box.width))
  const c = centerOf(box)
  const onCard = config.attachImagesToCards ? stickies.findLast((s) => inside(c, s.box)) : null
  const ext = detectImage(bytes)
  const image = {
    cardId: null,
    fileName: `${(node.name?.trim() || 'image').replace(/[\\/:*?"<>|]/g, '-').slice(0, 200)}.${ext}`.replace(/\.(png|jpe?g|gif|webp)\.\1$/i, '.$1'),
    caption: null,
    x: clamp(at(box.x, minX), -MAX_COORDINATE, MAX_COORDINATE),
    y: clamp(at(box.y, minY), -MAX_COORDINATE, MAX_COORDINATE),
    width,
    data: '',
    bucketId: section ? bucketOf.get(section).id : null,
    height,
  }
  const entry = { image, node, path, bytes: bytes.length, section, card: null }
  if (onCard) {
    entry.card = cardOf.get(onCard.node.id)
    entry.card.images.push(entry)
  }
  images.push(entry)
}

// Content on each card's own canvas: text on the left, then links below it, images to the right.
for (const item of cards) {
  const { card } = item
  const textBottom = card.components.reduce((y, c) => Math.max(y, c.y + c.height), 0)
  let imageY = 20
  for (const entry of item.images) {
    const w = Math.min(entry.image.width, 400)
    Object.assign(entry.image, {
      cardId: card.id,
      bucketId: null,
      x: card.components.length > 0 ? 440 : 20,
      y: imageY,
      height: Math.round((entry.image.height * w) / entry.image.width),
      width: Math.max(MIN_IMAGE_WIDTH, w),
    })
    imageY += entry.image.height + 20
  }
  if (card.components.length > MAX_COMPONENTS - 1) {
    warnings.push(`"${card.title}": text cut to ${MAX_COMPONENTS - 1} parts.`)
    card.components.length = MAX_COMPONENTS - 1
  }
  item.linkY = textBottom + 20
}

// ---- Parts: one paste each, a top-level section with everything in it always in one part ---------------------------
const topOf = (sectionId) => {
  let id = sectionId
  while (id && sections.find((s) => s.node.id === id)?.parent) id = sections.find((s) => s.node.id === id).parent
  return id
}
const units = new Map()
const unit = (key) => units.get(key) ?? units.set(key, { key, buckets: [], cards: [], images: [] }).get(key)
for (const b of buckets) unit(topOf(b.node.id)).buckets.push(b)
for (const c of cards) unit(c.section ? topOf(c.section) : `card:${c.node.id}`).cards.push(c)
for (const i of images) {
  if (i.card) unit(i.card.section ? topOf(i.card.section) : `card:${i.card.node.id}`).images.push(i)
  else unit(i.section ? topOf(i.section) : `image:${i.node.id}`).images.push(i)
}
const size = (u) => ({
  cards: u.cards.length, images: u.images.length, buckets: u.buckets.length, imageBytes: u.images.reduce((n, i) => n + i.bytes, 0),
})
const parts = []
for (const u of units.values()) {
  const s = size(u)
  const too = Object.keys(LIMITS).filter((k) => s[k] > LIMITS[k])
  if (too.length > 0) {
    const name = u.buckets[0]?.bucket.name ?? 'loose items'
    throw new Error(`Section "${name}" alone is over PrikBoard's paste limits (${too.map((k) => `${k} ${s[k]} > ${LIMITS[k]}`).join(', ')}).`)
  }
  const last = parts.at(-1)
  const fits = last && Object.keys(LIMITS).every((k) => last.size[k] + s[k] <= LIMITS[k])
  if (fits) {
    last.units.push(u)
    for (const k of Object.keys(LIMITS)) last.size[k] += s[k]
  } else {
    parts.push({ units: [u], size: s })
  }
}
if (allTags.size > MAX_BOARD_TAGS) {
  throw new Error(`${allTags.size} different tags; a board takes ${MAX_BOARD_TAGS}. Use fewer #hashtags or colorTags.`)
}

// ---- Write the clips --------------------------------------------------------------------------------------------
for (const old of readdirSync(out).filter((f) => /^part-\d+(\.files)?\.json$/.test(f))) rmSync(join(out, old))
const partOfCard = new Map()
parts.forEach((part, i) => part.units.forEach((u) => u.cards.forEach((c) => partOfCard.set(c, i))))
let crossPartLinks = 0
const written = parts.map((part, i) => {
  const partCards = part.units.flatMap((u) => u.cards)
  const partImages = part.units.flatMap((u) => u.images)
  const clip = {
    format: 'prikboard-clip',
    version: CLIP_VERSION,
    copiedAt: new Date().toISOString(),
    // Replaced by paste.mjs with the server and board it pastes on.
    source: { url: 'http://localhost', boardShortId: 'FIGJAM', boardKey: 'FIG' },
    cards: partCards.map((item) => {
      const links = item.links.filter((to) => partOfCard.get(to) === i)
      crossPartLinks += item.links.length - links.length
      const components = [...item.card.components]
      if (links.length > 0) {
        components.push({
          kind: 'Links',
          title: 'Connected in FigJam',
          links: links.slice(0, MAX_LINKS).map((to) => ({
            title: shorten(to.card.title || 'Card', 200),
            url: `/#/boards/${CLIP_BOARD_ID}/cards/${to.card.id}`,
          })),
          x: 20,
          y: item.linkY,
          width: 400,
          height: 40 + 40 * Math.min(links.length, MAX_LINKS),
        })
      }
      return { ...item.card, components }
    }),
    images: partImages.map((i) => i.image),
    drawings: [],
    buckets: part.units.flatMap((u) => u.buckets.map((b) => b.bucket)),
  }
  writeFileSync(join(out, `part-${i + 1}.json`), JSON.stringify(clip, null, 1))
  writeFileSync(join(out, `part-${i + 1}.files.json`), JSON.stringify(partImages.map((img) => img.path), null, 1))
  return clip
})

// ---- Report -----------------------------------------------------------------------------------------------------
const esc = (t) => String(t ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ')
const bucketName = (id) => buckets.find((b) => b.bucket.id === id)?.bucket.name ?? '(loose)'
const lines = [
  `# FigJam → PrikBoard: ${esc(file.name)}`,
  '',
  `Page "${esc(page.name)}", scale ${config.scale}. ${written.length} part${written.length === 1 ? '' : 's'} to paste.`,
  '',
  `- ${buckets.length} buckets from ${sections.length} sections${config.framesAsBuckets ? ' and frames' : ''}`,
  `- ${cards.length} cards from stickies, ${cards.reduce((n, c) => n + c.links.length, 0)} link(s) from connectors`,
  `- ${images.length} images (${images.filter((i) => i.card).length} attached to cards)`,
  `- ${allTags.size} tags: ${[...allTags].sort().join(', ') || 'none'}`,
  ...[...skipped].map(([why, n]) => `- left out: ${n} ${why}`),
  ...(droppedConnectors ? [`- left out: ${droppedConnectors} connectors not between two stickies`] : []),
  ...(crossPartLinks ? [`- left out: ${crossPartLinks} links between cards in different parts`] : []),
  '',
  ...(warnings.length ? ['## Warnings', '', ...warnings.map((w) => `- ${w}`), ''] : []),
  '## Parts',
  '',
  '| Part | Buckets | Cards | Images | Image MB |',
  '| --- | --- | --- | --- | --- |',
  ...parts.map((p, i) => `| part-${i + 1}.json | ${p.size.buckets} | ${p.size.cards} | ${p.size.images} | ${(p.size.imageBytes / 1e6).toFixed(1)} |`),
  '',
  '## Buckets',
  '',
  '| Figma node | Section | Bucket in | x, y | Size |',
  '| --- | --- | --- | --- | --- |',
  ...buckets.map(({ bucket, node, parent }) =>
    `| ${node.id} | ${esc(bucket.name)} | ${parent ? esc(bucketOf.get(parent).name) : ''} | ${bucket.x}, ${bucket.y} | ${bucket.width}×${bucket.height}${bucket.collapsed ? ' (collapsed)' : ''} |`),
  '',
  '## Cards',
  '',
  '| Figma node | Title | Sticky color → fill | Tags | Bucket | Images | Links |',
  '| --- | --- | --- | --- | --- | --- | --- |',
  ...cards.map(({ card, node, color, images: imgs, links }) =>
    `| ${node.id} | ${esc(card.title) || '(empty)'} | ${color} → ${card.color} | ${card.tags.join(', ')} | ${esc(bucketName(card.bucketId))} | ${imgs.length || ''} | ${links.length || ''} |`),
  '',
  '## Images',
  '',
  '| Figma node | File | On | Width |',
  '| --- | --- | --- | --- |',
  ...images.map((i) => `| ${i.node.id} | ${esc(i.image.fileName)} | ${i.card ? `card "${esc(i.card.card.title)}"` : esc(bucketName(i.image.bucketId))} | ${i.image.width} |`),
  '',
]
writeFileSync(join(out, 'report.md'), lines.join('\n'))

console.log(`${buckets.length} buckets, ${cards.length} cards, ${images.length} images in ${written.length} part(s) → ${out}`)
for (const w of warnings) console.log(`  warning: ${w}`)
console.log(`Check ${join(out, 'report.md')}, then: node tools/figma-import/paste.mjs --token=pb_pat_...`)
