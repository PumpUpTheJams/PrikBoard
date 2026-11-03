#!/usr/bin/env node
// Step 2 for a Miro board: turns what miro/fetch.mjs saved into PrikBoard clips and a report (see ../clips.mjs).
// Frames become buckets (and so do see-through rectangles drawn around things, named after the topmost text in them,
// as boards without frames often use), sticky notes and Miro cards become cards with their tags (and the tags' colors), images come
// along, and connectors between cards become links. Shapes, text, app cards, documents, embeds and anything else are
// left out and counted in the report. It doesn't call Miro.
//
//   node tools/board-import/miro/convert.mjs [--out=tools/board-import/out/miro] [--config=tools/board-import/config.json]
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { cardText, writeClips } from '../clips.mjs'
import { args, colorName, detectImage, hexColor, loadConfig } from '../shared.mjs'

const out = args.out ?? 'tools/board-import/out/miro'
const config = loadConfig('miro', {
  // 'auto': shrink so a typical sticky note becomes about one card and a grid step.
  scale: 'auto',
  // Put cards on a grid near where they were, so none overlap, and grow buckets to hold them.
  tidy: true,
  shapesAsBuckets: true,
  attachImagesToCards: true,
  includeAuthor: false,
  titleLength: 80,
  colorTags: {},
  colors: {},
  inks: {},
})

// Miro's sticky note colors → card fills.
const stickyFills = {
  gray: 'Butter', light_yellow: 'Butter', yellow: 'Butter', orange: 'Coral', light_green: 'Mint', green: 'Mint',
  dark_green: 'Mint', cyan: 'Mint', light_pink: 'Coral', pink: 'Coral', violet: 'Lilac', red: 'Coral', light_blue: 'Sky',
  blue: 'Sky', dark_blue: 'Sky', black: 'Butter', ...config.colors,
}
// Color names (from a hex color, as Miro cards have) → card fills.
const nameFills = {
  yellow: 'Butter', orange: 'Coral', red: 'Coral', pink: 'Coral', violet: 'Lilac', blue: 'Sky', teal: 'Mint', green: 'Mint',
  gray: 'Butter', white: 'Butter', black: 'Butter', ...config.colors,
}
// Miro's tag colors → PrikBoard tag colors.
const tagColors = {
  red: 'Brick', magenta: 'Berry', violet: 'Violet', blue: 'Ocean', dark_blue: 'Indigo', cyan: 'Teal', green: 'Pine',
  light_green: 'Moss', dark_green: 'Pine', yellow: 'Ochre', black: 'Ink', gray: 'Default',
}

const { board, items, connectors, tags, members, itemTags } = JSON.parse(readFileSync(join(out, 'miro-board.json'), 'utf8'))
const byId = new Map(items.map((i) => [i.id, i]))
const tagById = new Map(tags.map((t) => [t.id, t]))
const memberName = new Map(members.map((m) => [String(m.id), m.name]))
const model = { buckets: [], cards: [], images: [], drawings: [], skipped: new Map(), warnings: [], tagColors: {} }
const skip = (why) => model.skipped.set(why, (model.skipped.get(why) ?? 0) + 1)

// ---- Where things are: Miro gives an item's center, on the canvas or from its parent frame's top-left ----
const boxes = new Map()
function boxOf(item) {
  if (boxes.has(item.id)) return boxes.get(item.id)
  const width = item.geometry?.width ?? 0
  const height = item.geometry?.height ?? width
  let cx = item.position?.x ?? 0
  let cy = item.position?.y ?? 0
  const parent = item.parent?.id ? byId.get(String(item.parent.id)) : null
  if (parent && item.position?.relativeTo === 'parent_top_left') {
    const p = boxOf(parent)
    cx += p.x
    cy += p.y
  }
  const origin = item.position?.origin ?? 'center'
  const box = origin === 'center' ? { x: cx - width / 2, y: cy - height / 2, width, height } : { x: cx, y: cy, width, height }
  boxes.set(item.id, box)
  return box
}

/** Miro's rich text (HTML) as plain text with line breaks. */
function plain(html) {
  return String(html ?? '')
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/(p|div|li|h[1-6])>/gi, '\n')
    .replace(/<li[^>]*>/gi, '- ')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(Number(n)))
    .replace(/&#x([0-9a-f]+);/gi, (_, n) => String.fromCodePoint(parseInt(n, 16)))
    .replace(/&amp;/g, '&')
    .replace(/\n{3,}/g, '\n\n')
    .trim()
}

function tagsOf(item) {
  const ids = item.tagIds ?? item.data?.tagIds ?? itemTags?.[item.id] ?? []
  return ids.map((id) => tagById.get(String(id))).filter(Boolean)
}

// ---- Frames, and see-through rectangles drawn around things → buckets ----
const frames = items.filter((i) => i.type === 'frame')
const isArea = (i) =>
  config.shapesAsBuckets &&
  i.type === 'shape' &&
  ['rectangle', 'round_rectangle'].includes(i.data?.shape) &&
  !plain(i.data?.content) &&
  (Number(i.style?.fillOpacity ?? 1) < 0.3 || i.style?.fillColor === 'transparent') &&
  (i.geometry?.width ?? 0) > 0 &&
  (i.geometry?.height ?? 0) > 0
const areas = [...frames, ...items.filter(isArea)]
  .map((item) => ({ item, box: boxOf(item) }))
  // Biggest first: buckets inside others come later, so they stack on top.
  .sort((a, b) => b.box.width * b.box.height - a.box.width * a.box.height)
const contains = (outer, b, slack = 2) =>
  b.x >= outer.x - slack && b.y >= outer.y - slack && b.x + b.width <= outer.x + outer.width + slack && b.y + b.height <= outer.y + outer.height + slack
const inside = (p, b) => p.x >= b.x && p.x <= b.x + b.width && p.y >= b.y && p.y <= b.y + b.height
const centerOf = (b) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 })
/** The innermost bucket an item's center is in. */
function frameOf(item) {
  const c = centerOf(boxOf(item))
  return areas.findLast((a) => a.item !== item && inside(c, a.box))?.item.id ?? null
}
// A rectangle is named after the topmost text inside it (and not inside a smaller one); that text isn't left out.
const namedBy = new Set()
for (const { item, box } of areas) {
  let name = plain(item.data?.title)
  if (!name) {
    const heading = items
      .filter((t) => t.type === 'text' && !namedBy.has(t.id) && frameOf(t) === item.id && plain(t.data?.content))
      .sort((a, b) => boxOf(a).y - boxOf(b).y)[0]
    if (heading) {
      namedBy.add(heading.id)
      name = plain(heading.data.content).replace(/\s+/g, ' ').replace(/:\s*$/, '').trim()
    }
  }
  const parent = areas.findLast((a) => a.item !== item && a.box.width * a.box.height > box.width * box.height && contains(a.box, box))
  model.buckets.push({ key: item.id, name: name || (item.type === 'frame' ? 'Frame' : 'Area'), box, parent: parent?.item.id ?? null, collapsed: false })
}

// ---- Sticky notes and Miro cards → cards ----
for (const item of items) {
  if (item.type !== 'sticky_note' && item.type !== 'card') continue
  const tagList = tagsOf(item)
  for (const t of tagList) if (tagColors[t.fillColor]) model.tagColors[t.title] = tagColors[t.fillColor]
  let title
  let body
  let hashtags
  let color
  let colorLabel
  if (item.type === 'sticky_note') {
    ;({ title, body, hashtags } = cardText(plain(item.data?.content), config.titleLength))
    colorLabel = item.style?.fillColor ?? 'light_yellow'
    color = stickyFills[colorLabel] ?? 'Butter'
  } else {
    const heading = plain(item.data?.title)
    const description = plain(item.data?.description)
    ;({ title, body, hashtags } = cardText([heading, description].filter(Boolean).join('\n'), config.titleLength))
    const extra = [
      item.data?.dueDate ? `Due ${String(item.data.dueDate).slice(0, 10)}` : '',
      item.data?.assigneeId ? `Assigned to ${memberName.get(String(item.data.assigneeId)) ?? 'someone'} in Miro` : '',
    ].filter(Boolean)
    if (extra.length) body = [body || (title !== heading ? heading : ''), extra.join('\n')].filter(Boolean).join('\n\n')
    const rgb = hexColor(item.style?.cardTheme)
    colorLabel = item.style?.cardTheme ?? 'card'
    color = rgb ? nameFills[colorName(rgb)] ?? 'Butter' : 'Butter'
  }
  const creator = config.includeAuthor && item.createdBy?.id ? memberName.get(String(item.createdBy.id)) : null
  model.cards.push({
    key: item.id,
    title,
    text: [body, creator ? `From Miro, by ${creator}` : ''].filter(Boolean).join('\n\n'),
    color,
    colorLabel,
    tags: [...tagList.map((t) => t.title), ...hashtags, config.colorTags[colorLabel]].filter(Boolean),
    box: boxOf(item),
    bucket: frameOf(item),
    links: [],
  })
}

// ---- Connectors between cards → links on the card they start from ----
const cardKeys = new Set(model.cards.map((c) => c.key))
let droppedConnectors = 0
for (const connector of connectors) {
  const from = String(connector.startItem?.id ?? '')
  const to = String(connector.endItem?.id ?? '')
  if (!cardKeys.has(from) || !cardKeys.has(to) || from === to) droppedConnectors++
  else model.cards.find((c) => c.key === from).links.push(to)
}
if (droppedConnectors) model.skipped.set('connectors not between two cards', droppedConnectors)

// ---- Images (attached to the card they sit on) ----
for (const item of items.filter((i) => i.type === 'image')) {
  const path = join(out, 'images', item.id)
  const bytes = existsSync(path) ? readFileSync(path) : null
  if (!bytes || !detectImage(bytes)) {
    model.warnings.push(`Image ${item.id} left out: no file PrikBoard can take. Run miro/fetch.mjs again?`)
    continue
  }
  const box = boxOf(item)
  if (!box.width || !box.height) {
    model.warnings.push(`Image ${item.id} left out: Miro gave no size.`)
    continue
  }
  const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 }
  const onCard = config.attachImagesToCards ? model.cards.findLast((c) => inside(center, c.box)) : null
  const name = `${(plain(item.data?.title) || 'image').replace(/[\\/:*?"<>|]/g, '-').slice(0, 200)}.${detectImage(bytes)}`
    .replace(/\.(png|jpe?g|gif|webp)\.\1$/i, '.$1')
  model.images.push({ key: item.id, name, box, bucket: frameOf(item), card: onCard?.key ?? null, path })
}

// ---- Everything else ----
const kept = new Set(['frame', 'sticky_note', 'card', 'image'])
const areaIds = new Set(areas.map((a) => a.item.id))
for (const item of items) {
  if (kept.has(item.type) || areaIds.has(item.id) || namedBy.has(item.id)) continue
  // Miro's API doesn't give the lines of freehand drawings (paint), only where they are.
  if (item.type === 'paint') skip("drawings (Miro's API gives no lines for them: copy them as an image in Miro instead)")
  else skip(`${item.type.replaceAll('_', ' ')} items`)
}

// Miro sticky notes come in every size; PrikBoard cards are all 200 × 120. 'auto' shrinks the board so a typical
// sticky note's area becomes a card's, which keeps how full the buckets look; tidy then keeps cards from overlapping.
const stickyAreas = items
  .filter((i) => i.type === 'sticky_note' && i.geometry?.width)
  .map((i) => i.geometry.width * (i.geometry.height ?? i.geometry.width))
  .sort((a, b) => a - b)
const scale =
  config.scale === 'auto'
    ? stickyAreas.length
      ? Math.round(Math.min(1.5, Math.max(0.2, Math.sqrt((200 * 120) / stickyAreas[Math.floor(stickyAreas.length / 2)]))) * 100) / 100
      : 1
    : Number(config.scale)

writeClips(model, {
  out,
  scale,
  tidy: config.tidy,
  source: 'Miro',
  sourceName: board.name,
  idLabel: 'Miro item',
  from: 'miro',
  linkTitle: 'Connected in Miro',
  summary: [
    `${frames.length} frames and ${areas.length - frames.length} rectangles → buckets, ${items.filter((i) => i.type === 'sticky_note').length} sticky notes and ${items.filter((i) => i.type === 'card').length} Miro cards → cards`,
  ],
})
