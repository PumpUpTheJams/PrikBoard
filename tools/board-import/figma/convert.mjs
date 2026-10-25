#!/usr/bin/env node
// Step 2 for a FigJam board: turns what figma/fetch.mjs saved into PrikBoard clips and a report (see ../clips.mjs).
// Sections (and frames) become buckets, stickies become cards, images come along, connectors between stickies become
// links, and pen strokes become drawings (or pictures, when Figma only gave their outline). Shapes with text, loose
// text and everything else are left out and counted in the report. It doesn't call Figma.
//
//   node tools/board-import/figma/convert.mjs [--out=tools/board-import/out/figma] [--config=tools/board-import/config.json]
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { cardText, writeClips } from '../clips.mjs'
import { inkFor } from '../drawings.mjs'
import { args, colorName, detectImage, eachNode, hexColor, loadConfig, MAX_IMAGE_BYTES, safeId } from '../shared.mjs'
import { isPen, penLines, penStyle, svgLines } from './pen.mjs'

const out = args.out ?? 'tools/board-import/out/figma'
const config = loadConfig('figma', {
  page: null,
  scale: 0.85,
  framesAsBuckets: true,
  tidy: false,
  attachImagesToCards: true,
  includeAuthor: true,
  titleLength: 80,
  colorTags: {},
  colors: {},
  inks: {},
})
const fills = {
  yellow: 'Butter', orange: 'Coral', red: 'Coral', pink: 'Coral', violet: 'Lilac', blue: 'Sky', teal: 'Mint', green: 'Mint',
  gray: 'Butter', white: 'Butter', black: 'Butter', ...config.colors,
}

const file = JSON.parse(readFileSync(join(out, 'figma-file.json'), 'utf8'))
const pages = file.document.children
const page = config.page == null ? pages[0] : pages.find((p) => p.name === config.page) ?? pages[Number(config.page)]
if (!page) throw new Error(`No page "${config.page}". Pages: ${pages.map((p) => p.name).join(', ')}`)

const model = { buckets: [], cards: [], images: [], drawings: [], skipped: new Map(), warnings: [] }
const skip = (why) => model.skipped.set(why, (model.skipped.get(why) ?? 0) + 1)
const isSection = (n) => n.type === 'SECTION' || (config.framesAsBuckets && n.type === 'FRAME')
const containers = new Set(['CANVAS', 'GROUP', 'SECTION', 'FRAME', 'COMPONENT', 'COMPONENT_SET', 'INSTANCE', 'BOOLEAN_OPERATION'])
const stickies = []
const connectors = []
const imageNodes = []
const pens = []

for (const node of eachNode(page)) {
  if (node === page) continue
  const bucket = node.ancestors.findLast(isSection)?.id ?? null
  const box = node.absoluteBoundingBox
  const imageFill = (node.fills ?? []).find((f) => f.type === 'IMAGE' && f.imageRef && f.visible !== false)
  if (isSection(node) && box) {
    model.buckets.push({ key: node.id, name: node.name || 'Section', box, parent: bucket, collapsed: node.sectionContentsHidden === true })
  } else if (node.type === 'STICKY' && box) {
    stickies.push({ node, box, bucket })
  } else if (node.type === 'CONNECTOR') {
    connectors.push(node)
  } else if (imageFill && box) {
    imageNodes.push({ node, box, bucket, ref: imageFill.imageRef })
  } else if (isPen(node) && box) {
    pens.push({ node, box, bucket })
  } else if (node.type === 'SHAPE_WITH_TEXT') {
    skip('shapes with text')
  } else if (node.type === 'TEXT') {
    if (!node.ancestors.some((a) => a.type === 'STICKY')) skip('loose text')
  } else if (!containers.has(node.type)) {
    skip(`${node.type.toLowerCase().replaceAll('_', ' ')} nodes`)
  }
}

// ---- Stickies → cards ----
const inside = (p, b) => p.x >= b.x && p.x <= b.x + b.width && p.y >= b.y && p.y <= b.y + b.height
for (const { node, box, bucket } of stickies) {
  const fill = (node.fills ?? []).find((f) => f.type === 'SOLID' && f.visible !== false)
  const color = fill ? colorName(fill.color) : 'yellow'
  const { title, body, hashtags } = cardText(node.characters ?? '', config.titleLength)
  const author = config.includeAuthor && node.authorVisible !== false && node.authorName ? `From FigJam, by ${node.authorName}` : ''
  model.cards.push({
    key: node.id,
    title,
    text: [body, author].filter(Boolean).join('\n\n'),
    color: fills[color] ?? 'Butter',
    colorLabel: color,
    tags: [...hashtags, config.colorTags[color]].filter(Boolean),
    box,
    bucket,
    links: [],
  })
}

// ---- Connectors between stickies → links on the card they start from ----
const cardKeys = new Set(model.cards.map((c) => c.key))
let droppedConnectors = 0
for (const connector of connectors) {
  const from = connector.connectorStart?.endpointNodeId
  const to = connector.connectorEnd?.endpointNodeId
  if (!cardKeys.has(from) || !cardKeys.has(to) || from === to) droppedConnectors++
  else model.cards.find((c) => c.key === from).links.push(to)
}
if (droppedConnectors) model.skipped.set('connectors not between two stickies', droppedConnectors)

// ---- Images, and pictures of pen strokes that can't be drawn again ----
function picture(node, box, bucket, candidates) {
  for (const path of candidates) {
    const bytes = existsSync(path) ? readFileSync(path) : null
    if (!bytes || !detectImage(bytes) || bytes.length > MAX_IMAGE_BYTES) continue
    const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 }
    const onCard = config.attachImagesToCards && !isPen(node) ? stickies.findLast((s) => inside(center, s.box)) : null
    const ext = detectImage(bytes)
    const name = `${(node.name?.trim() || 'image').replace(/[\\/:*?"<>|]/g, '-').slice(0, 200)}.${ext}`.replace(/\.(png|jpe?g|gif|webp)\.\1$/i, '.$1')
    model.images.push({ key: node.id, name, box, bucket, card: onCard?.node.id ?? null, path })
    return true
  }
  return false
}
for (const { node, box, bucket, ref } of imageNodes) {
  if (!picture(node, box, bucket, [join(out, 'images', ref), join(out, 'renders', `${safeId(node.id)}.jpg`)])) {
    model.warnings.push(`Image "${node.name}" (${node.id}) left out: no file PrikBoard can take. Run figma/fetch.mjs again?`)
  }
}

// ---- Pen strokes → drawings; strokes grouped together in FigJam stay one drawing ----
const drawingOf = new Map()
let pictures = 0
let drawn = 0
for (const { node, box, bucket } of pens) {
  const { color, width } = penStyle(node)
  const svgPath = join(out, 'renders', `${safeId(node.id)}.svg`)
  // The path data's own lines, else the SVG export's, each with its color and width.
  const strokes =
    penLines(node)?.map((points) => ({ color, width, points })) ??
    (existsSync(svgPath) ? svgLines(readFileSync(svgPath, 'utf8'), node) : null)?.map((s) => ({ ...s, color: hexColor(s.color) ?? color }))
  if (!strokes) {
    if (picture(node, box, bucket, [join(out, 'renders', `${safeId(node.id)}.png`)])) pictures++
    else model.warnings.push(`Pen stroke ${node.id} left out: no path data and no picture. Run figma/fetch.mjs again?`)
    continue
  }
  const parent = node.ancestors.at(-1)
  const grouped = parent?.type === 'GROUP' && parent.children.every((c) => isPen(c) || c.visible === false)
  const key = grouped ? parent.id : node.id
  if (!drawingOf.has(key)) {
    const drawing = { key, box: grouped ? parent.absoluteBoundingBox ?? box : box, bucket, strokes: [] }
    drawingOf.set(key, drawing)
    model.drawings.push(drawing)
  }
  drawn++
  for (const s of strokes) drawingOf.get(key).strokes.push({ ink: inkFor(s.color, config.inks).ink, width: s.width, points: s.points })
}

writeClips(model, {
  out,
  scale: config.scale,
  tidy: config.tidy,
  source: 'FigJam',
  sourceName: `${file.name}, page "${page.name}"`,
  idLabel: 'Figma node',
  from: 'figma',
  linkTitle: 'Connected in FigJam',
  summary: [
    `${model.buckets.length} sections${config.framesAsBuckets ? ' and frames' : ''} → buckets, ${stickies.length} stickies → cards`,
    ...(pens.length ? [`${pens.length} pen strokes: ${drawn} as drawings, ${pictures} as pictures`] : []),
  ],
})
