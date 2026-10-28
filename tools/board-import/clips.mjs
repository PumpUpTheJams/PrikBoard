// What every converter shares: it reads its own source into a board model (below), and writeClips turns that into
// PrikBoard clips (the format pasteItems takes), one per part, with a part-N.files.json listing each image's file, and
// report.md, the mapping from source items to buckets and cards.
//
// The model, in the source's own coordinates (absolute, top-left corners, y down, before scaling):
//   buckets:  { key, name, box, parent, collapsed }            parent: key of the bucket it sits in; outer ones first
//   cards:    { key, title, text, color, colorLabel, tags, box, bucket, links }
//             color: a PrikBoard fill; colorLabel: the source's color, for the report; links: keys of other cards
//   images:   { key, name, box, bucket, card, path }           card: key of the card it is attached to, or null
//   drawings: { key, box, bucket, strokes: [{ ink, width, points: [[x, y], …] }] }
//   tagColors: { tag: TagColor }, skipped: Map(why → count), warnings: [text]
// A box is { x, y, width, height }.
import { readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { thinOut, snapWidth } from './drawings.mjs'
import { uuidFrom } from './shared.mjs'

// PrikBoard's limits (src/PrikBoard.Api: Layout, BoardClip, BoardDrawing, CardComponent, CardTags, BoardTag).
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
const MAX_DRAWING_SIZE = 10_000
const MAX_STROKES = 1000
const MAX_DRAWING_POINTS = 50_000
const LIMITS = { cards: 2000, images: 200, buckets: 100, drawings: 5000, imageBytes: 150 * 1024 * 1024 }
const CLIP_VERSION = 6
// Card links in a clip name cards by id under some board id; the paste points them at the new cards.
const CLIP_BOARD_ID = uuidFrom('figjam-import-board')

export const normalizeTag = (t) => t.trim().toLowerCase().replace(/\s+/g, '-').slice(0, MAX_TAG_LENGTH)

/** A card's title and the rest of its text from what the source holds, with #hashtags taken out as tags. */
export function cardText(raw, titleLength = 80) {
  const clean = raw.replace(/\r\n?/g, '\n').trim()
  const hashtags = [...clean.matchAll(/(^|\s)#([\p{L}\p{N}][\p{L}\p{N}_-]*)/gu)].map((m) => m[2])
  const text = clean.replace(/(^|[ \t])#[\p{L}\p{N}][\p{L}\p{N}_-]*/gu, '$1').replace(/[ \t]+$/gm, '').replace(/\n{3,}/g, '\n\n').trim()
  const lines = text.split('\n').map((l) => l.trim()).filter(Boolean)
  const title = shorten(lines[0] ?? '', Math.min(MAX_TITLE, titleLength))
  const body = lines.length > 1 || title !== (lines[0] ?? '') ? text : ''
  return { title, body, hashtags }
}

export function shorten(line, size) {
  if (line.length <= size) return line
  const cut = line.lastIndexOf(' ', size - 1)
  return `${line.slice(0, cut > size / 2 ? cut : size - 1).trimEnd()}…`
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

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v))
const centerOf = (b) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 })

/**
 * Writes the clips and the report for a board model.
 * options: { out, scale, source (e.g. "FigJam"), sourceName (the board's name there), idLabel (e.g. "Figma node"),
 *            from (the folder name paste.mjs takes), linkTitle, summary (extra lines for the report),
 *            tidy (put cards on a grid so none overlap, see tidyCards) }
 */
export function writeClips(model, options) {
  const { out, scale } = options
  const warnings = [...(model.warnings ?? [])]
  const skipped = model.skipped ?? new Map()

  // ---- Coordinates: shift so the top-left item is at 0,0, and scale ----
  const boxes = [...model.buckets, ...model.cards, ...model.images, ...model.drawings].map((i) => i.box)
  if (boxes.length === 0) throw new Error('Nothing to move: no buckets, cards, images or drawings.')
  const minX = Math.min(...boxes.map((b) => b.x))
  const minY = Math.min(...boxes.map((b) => b.y))
  const atX = (v) => clamp(Math.round((v - minX) * scale), -MAX_COORDINATE, MAX_COORDINATE)
  const atY = (v) => clamp(Math.round((v - minY) * scale), -MAX_COORDINATE, MAX_COORDINATE)
  const px = (v) => Math.round(v * scale)

  // ---- Buckets ----
  const bucketOf = new Map()
  const buckets = model.buckets.map((b) => {
    const bucket = {
      id: uuidFrom(`bucket:${b.key}`),
      name: (b.name?.trim() || 'Bucket').slice(0, 100),
      x: atX(b.box.x),
      y: atY(b.box.y),
      width: clamp(px(b.box.width), MIN_BUCKET_WIDTH, MAX_BUCKET_SIZE),
      height: clamp(px(b.box.height), MIN_BUCKET_HEIGHT, MAX_BUCKET_SIZE),
      collapsed: b.collapsed === true,
    }
    if (px(b.box.width) > MAX_BUCKET_SIZE || px(b.box.height) > MAX_BUCKET_SIZE) {
      warnings.push(`Bucket "${bucket.name}" (${b.key}) is ${px(b.box.width)}×${px(b.box.height)} px; a bucket is at most ${MAX_BUCKET_SIZE}, so it was cut to ${bucket.width}×${bucket.height}. What stuck out stays in it.`)
    }
    bucketOf.set(b.key, bucket)
    return { bucket, source: b }
  })
  const bucketId = (key) => (key ? bucketOf.get(key)?.id ?? null : null)

  // ---- Cards ----
  const allTags = new Set()
  const cardOf = new Map()
  const cards = model.cards.map((c, index) => {
    const tags = [...new Set(c.tags.map(normalizeTag).filter(Boolean))]
    if (tags.length > MAX_TAGS_PER_CARD) warnings.push(`"${c.title}" has ${tags.length} tags; kept the first ${MAX_TAGS_PER_CARD}.`)
    const kept = tags.slice(0, MAX_TAGS_PER_CARD)
    kept.forEach((t) => allTags.add(t))
    const center = centerOf(c.box)
    const card = {
      id: uuidFrom(`card:${c.key}`),
      number: index + 1,
      title: c.title,
      color: c.color,
      tags: kept,
      x: clamp(atX(center.x) - CARD_WIDTH / 2, -MAX_COORDINATE, MAX_COORDINATE),
      y: clamp(atY(center.y) - CARD_HEIGHT / 2, -MAX_COORDINATE, MAX_COORDINATE),
      components: [],
      bucketId: bucketId(c.bucket),
    }
    let y = 20
    for (const chunk of split(c.text ?? '', MAX_TEXT)) {
      // About 50 characters a line at 400 px wide, 20 px a line.
      const rows = chunk.split('\n').reduce((n, line) => n + Math.max(1, Math.ceil(line.length / 50)), 0)
      const height = clamp(rows * 20 + 60, 80, 2000)
      card.components.push({ kind: 'Text', text: chunk, x: 20, y, width: 400, height })
      y += height + 20
    }
    const item = { card, source: c, images: [], links: [] }
    cardOf.set(c.key, item)
    return item
  })
  for (const item of cards) {
    item.links = [...new Set(item.source.links ?? [])].map((key) => cardOf.get(key)).filter((to) => to && to !== item)
  }
  if (options.tidy) tidyCards(cards, buckets, model.images.filter((i) => !i.card).map((i) => ({
    x: atX(i.box.x), y: atY(i.box.y), width: px(i.box.width), height: px(i.box.height),
  })), warnings)

  // ---- Images ----
  const images = model.images.map((i) => {
    const width = clamp(px(i.box.width), MIN_IMAGE_WIDTH, MAX_IMAGE_WIDTH)
    const height = Math.max(1, Math.round((width * i.box.height) / i.box.width))
    const bytes = readFileSync(i.path).length
    const image = {
      cardId: null,
      fileName: i.name,
      caption: null,
      x: atX(i.box.x),
      y: atY(i.box.y),
      width,
      data: '',
      bucketId: bucketId(i.bucket),
      height,
    }
    const entry = { image, source: i, path: i.path, bytes, card: i.card ? cardOf.get(i.card) ?? null : null }
    entry.card?.images.push(entry)
    return entry
  })

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

  // ---- Drawings: points relative to the drawing's corner, which sits on the 20 px grid ----
  const drawings = []
  for (const d of model.drawings) {
    const strokes = d.strokes
      .map((s) => ({ ...s, points: s.points.map(([x, y]) => [(x - minX) * scale, (y - minY) * scale]) }))
      .filter((s) => s.points.length > 0)
      .slice(0, MAX_STROKES)
    if (strokes.length === 0) continue
    const all = strokes.flatMap((s) => s.points)
    const x = Math.floor(Math.min(...all.map((p) => p[0])) / 20) * 20
    const y = Math.floor(Math.min(...all.map((p) => p[1])) / 20) * 20
    const tooBig = all.some((p) => p[0] - x > MAX_DRAWING_SIZE || p[1] - y > MAX_DRAWING_SIZE)
    if (tooBig) {
      warnings.push(`Drawing ${d.key} left out: over ${MAX_DRAWING_SIZE} px across.`)
      continue
    }
    const budget = Math.max(2, Math.floor(MAX_DRAWING_POINTS / strokes.length))
    const drawing = {
      cardId: null,
      x,
      y,
      strokes: strokes.map((s) => ({
        color: s.ink,
        strokeWidth: snapWidth(s.width * scale),
        points: thinOut(s.points, budget).map(([px_, py]) => [round1(px_ - x), round1(py - y), 0.5]),
      })),
      bucketId: bucketId(d.bucket),
    }
    drawings.push({ drawing, source: d })
  }

  // ---- Parts: one paste each, a top-level bucket with everything in it always in one part ----
  const parentOf = new Map(model.buckets.map((b) => [b.key, b.parent]))
  const topOf = (key) => {
    let id = key
    while (id && parentOf.get(id)) id = parentOf.get(id)
    return id
  }
  const units = new Map()
  const unit = (key) => units.get(key) ?? units.set(key, { key, buckets: [], cards: [], images: [], drawings: [] }).get(key)
  for (const b of buckets) unit(topOf(b.source.key)).buckets.push(b)
  for (const c of cards) unit(c.source.bucket ? topOf(c.source.bucket) : `card:${c.source.key}`).cards.push(c)
  for (const i of images) {
    if (i.card) unit(i.card.source.bucket ? topOf(i.card.source.bucket) : `card:${i.card.source.key}`).images.push(i)
    else unit(i.source.bucket ? topOf(i.source.bucket) : `image:${i.source.key}`).images.push(i)
  }
  for (const d of drawings) unit(d.source.bucket ? topOf(d.source.bucket) : `drawing:${d.source.key}`).drawings.push(d)
  const size = (u) => ({
    cards: u.cards.length,
    images: u.images.length,
    buckets: u.buckets.length,
    drawings: u.drawings.length,
    imageBytes: u.images.reduce((n, i) => n + i.bytes, 0),
  })
  const parts = []
  for (const u of units.values()) {
    const s = size(u)
    const too = Object.keys(LIMITS).filter((k) => s[k] > LIMITS[k])
    if (too.length > 0) {
      const name = u.buckets[0]?.bucket.name ?? 'loose items'
      throw new Error(`Bucket "${name}" alone is over PrikBoard's paste limits (${too.map((k) => `${k} ${s[k]} > ${LIMITS[k]}`).join(', ')}).`)
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
  const tagColors = Object.fromEntries(
    Object.entries(model.tagColors ?? {}).map(([t, c]) => [normalizeTag(t), c]).filter(([t, c]) => allTags.has(t) && c !== 'Default'),
  )

  // ---- Write the clips ----
  for (const old of readdirSync(out).filter((f) => /^part-\d+(\.files)?\.json$/.test(f))) rmSync(join(out, old))
  const partOfCard = new Map()
  parts.forEach((part, i) => part.units.forEach((u) => u.cards.forEach((c) => partOfCard.set(c, i))))
  let crossPartLinks = 0
  parts.forEach((part, i) => {
    const partCards = part.units.flatMap((u) => u.cards)
    const partImages = part.units.flatMap((u) => u.images)
    const partTags = new Set(partCards.flatMap((c) => c.card.tags))
    const colors = Object.fromEntries(Object.entries(tagColors).filter(([t]) => partTags.has(t)))
    const clip = {
      format: 'prikboard-clip',
      version: CLIP_VERSION,
      copiedAt: new Date().toISOString(),
      // Replaced by paste.mjs with the server it pastes on.
      source: { url: 'http://localhost', boardShortId: 'FIGJAM', boardKey: 'FIG' },
      cards: partCards.map((item) => {
        const links = item.links.filter((to) => partOfCard.get(to) === i)
        crossPartLinks += item.links.length - links.length
        const components = [...item.card.components]
        if (links.length > 0) {
          components.push({
            kind: 'Links',
            title: options.linkTitle,
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
      images: partImages.map((img) => img.image),
      drawings: part.units.flatMap((u) => u.drawings.map((d) => d.drawing)),
      ...(Object.keys(colors).length > 0 ? { tagColors: colors } : {}),
      buckets: part.units.flatMap((u) => u.buckets.map((b) => b.bucket)),
    }
    writeFileSync(join(out, `part-${i + 1}.json`), JSON.stringify(clip, null, 1))
    writeFileSync(join(out, `part-${i + 1}.files.json`), JSON.stringify(partImages.map((img) => img.path), null, 1))
  })

  // ---- Report ----
  const esc = (t) => String(t ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ')
  const bucketName = (id) => buckets.find((b) => b.bucket.id === id)?.bucket.name ?? '(loose)'
  const id = options.idLabel
  const lines = [
    `# ${options.source} → PrikBoard: ${esc(options.sourceName)}`,
    '',
    `Scale ${scale}${options.tidy ? ', cards tidied onto a grid' : ''}. ${parts.length} part${parts.length === 1 ? '' : 's'} to paste.`,
    '',
    ...(options.summary ?? []).map((s) => `- ${s}`),
    `- ${buckets.length} buckets, ${cards.length} cards, ${cards.reduce((n, c) => n + c.links.length, 0)} link(s) from connectors`,
    `- ${images.length} images (${images.filter((i) => i.card).length} attached to cards)`,
    `- ${drawings.length} drawings (${drawings.reduce((n, d) => n + d.drawing.strokes.length, 0)} strokes)`,
    `- ${allTags.size} tags: ${[...allTags].sort().map((t) => (tagColors[t] ? `${t} (${tagColors[t]})` : t)).join(', ') || 'none'}`,
    ...[...skipped].map(([why, n]) => `- left out: ${n} ${why}`),
    ...(crossPartLinks ? [`- left out: ${crossPartLinks} links between cards in different parts`] : []),
    '',
    ...(warnings.length ? ['## Warnings', '', ...warnings.map((w) => `- ${w}`), ''] : []),
    '## Parts',
    '',
    '| Part | Buckets | Cards | Images | Drawings | Image MB |',
    '| --- | --- | --- | --- | --- | --- |',
    ...parts.map((p, i) =>
      `| part-${i + 1}.json | ${p.size.buckets} | ${p.size.cards} | ${p.size.images} | ${p.size.drawings} | ${(p.size.imageBytes / 1e6).toFixed(1)} |`),
    '',
    '## Buckets',
    '',
    `| ${id} | Bucket | Inside | x, y | Size |`,
    '| --- | --- | --- | --- | --- |',
    ...buckets.map(({ bucket, source }) =>
      `| ${source.key} | ${esc(bucket.name)} | ${source.parent ? esc(bucketOf.get(source.parent)?.name) : ''} | ${bucket.x}, ${bucket.y} | ${bucket.width}×${bucket.height}${bucket.collapsed ? ' (collapsed)' : ''} |`),
    '',
    '## Cards',
    '',
    `| ${id} | Title | Color → fill | Tags | Bucket | Images | Links |`,
    '| --- | --- | --- | --- | --- | --- | --- |',
    ...cards.map(({ card, source, images: imgs, links }) =>
      `| ${source.key} | ${esc(card.title) || '(empty)'} | ${esc(source.colorLabel)} → ${card.color} | ${card.tags.join(', ')} | ${esc(bucketName(card.bucketId))} | ${imgs.length || ''} | ${links.length || ''} |`),
    '',
    '## Images',
    '',
    `| ${id} | File | On | Width |`,
    '| --- | --- | --- | --- |',
    ...images.map((i) => `| ${i.source.key} | ${esc(i.image.fileName)} | ${i.card ? `card "${esc(i.card.card.title)}"` : esc(bucketName(i.image.bucketId))} | ${i.image.width} |`),
    '',
    ...(drawings.length
      ? ['## Drawings', '', `| ${id} | In | Strokes | Inks |`, '| --- | --- | --- | --- |',
          ...drawings.map(({ drawing, source }) =>
            `| ${source.key} | ${esc(bucketName(drawing.bucketId))} | ${drawing.strokes.length} | ${[...new Set(drawing.strokes.map((s) => s.color))].join(', ')} |`), '']
      : []),
  ]
  writeFileSync(join(out, 'report.md'), lines.join('\n'))

  console.log(`${buckets.length} buckets, ${cards.length} cards, ${images.length} images, ${drawings.length} drawings in ${parts.length} part(s) → ${out}`)
  for (const w of warnings) console.log(`  warning: ${w}`)
  console.log(`Check ${join(out, 'report.md')}, then: node tools/board-import/paste.mjs --from=${options.from} --board=<short id>`)
}

/**
 * Puts every card in the nearest free spot of a grid of card-sized cells (a grid step apart) near where it was, so no
 * two cards overlap and none covers a loose image: cards in a bucket on a grid inside that bucket, below room for its
 * name; loose cards on the board's grid. Top rows choose first. A bucket then grows to hold its cards.
 */
function tidyCards(cards, buckets, obstacles, warnings) {
  const CELL_W = CARD_WIDTH + 20
  const CELL_H = CARD_HEIGHT + 20
  const snap = (v) => Math.round(v / 20) * 20
  // Placed rectangles in a coarse hash, so checking a spot looks only at its neighbors.
  const HASH = 400
  const hash = new Map()
  const keysOf = (r) => {
    const keys = []
    for (let gx = Math.floor(r.x / HASH); gx <= Math.floor((r.x + r.width) / HASH); gx++) {
      for (let gy = Math.floor(r.y / HASH); gy <= Math.floor((r.y + r.height) / HASH); gy++) keys.push(`${gx},${gy}`)
    }
    return keys
  }
  const add = (r) => keysOf(r).forEach((k) => hash.set(k, [...(hash.get(k) ?? []), r]))
  const free = (r) => keysOf(r).every((k) => (hash.get(k) ?? []).every((o) =>
    r.x >= o.x + o.width || o.x >= r.x + r.width || r.y >= o.y + o.height || o.y >= r.y + r.height))
  obstacles.forEach(add)

  const bucketById = new Map(buckets.map((b) => [b.bucket.id, b.bucket]))
  let moved = 0
  for (const item of [...cards].sort((a, b) => a.card.y - b.card.y || a.card.x - b.card.x)) {
    const { card } = item
    const bucket = card.bucketId ? bucketById.get(card.bucketId) : null
    const ox = bucket ? snap(bucket.x) + 20 : 0
    const oy = bucket ? snap(bucket.y) + 60 : 0
    const inside = (x, y) => !bucket || (x + CARD_WIDTH <= bucket.x + bucket.width - 20 && y + CARD_HEIGHT <= bucket.y + bucket.height - 20)
    const col0 = Math.round((card.x - ox) / CELL_W)
    const row0 = Math.round((card.y - oy) / CELL_H)
    let best = null
    for (let ring = 0; ring < 200; ring++) {
      // Nothing in this ring or beyond can beat what was found.
      if (best && ring * Math.min(CELL_W, CELL_H) > best.cost) break
      for (let col = col0 - ring; col <= col0 + ring; col++) {
        for (let row = row0 - ring; row <= row0 + ring; row++) {
          if (Math.max(Math.abs(col - col0), Math.abs(row - row0)) !== ring) continue
          if (bucket && (col < 0 || row < 0)) continue
          const x = ox + col * CELL_W
          const y = oy + row * CELL_H
          // A spot outside the bucket is taken only when the bucket is full; the bucket grows to it.
          const cost = Math.hypot(x - card.x, y - card.y) + (inside(x, y) ? 0 : 100_000)
          if ((!best || cost < best.cost) && free({ x, y, width: CELL_W, height: CELL_H })) best = { x, y, cost }
        }
      }
    }
    if (!best) continue
    if (best.x !== card.x || best.y !== card.y) moved++
    card.x = best.x
    card.y = best.y
    add({ x: best.x, y: best.y, width: CELL_W, height: CELL_H })
  }

  for (const { bucket } of buckets) {
    const own = cards.map((c) => c.card).filter((c) => c.bucketId === bucket.id)
    if (own.length === 0) continue
    const left = Math.min(bucket.x, ...own.map((c) => c.x - 20))
    const top = Math.min(bucket.y, ...own.map((c) => c.y - 60))
    const right = Math.max(bucket.x + bucket.width, ...own.map((c) => c.x + CARD_WIDTH + 20))
    const bottom = Math.max(bucket.y + bucket.height, ...own.map((c) => c.y + CARD_HEIGHT + 20))
    if (right - left > MAX_BUCKET_SIZE || bottom - top > MAX_BUCKET_SIZE) {
      warnings.push(`Bucket "${bucket.name}" would need more than ${MAX_BUCKET_SIZE} px for its cards; some stick out.`)
    }
    Object.assign(bucket, {
      x: left,
      y: top,
      width: clamp(right - left, MIN_BUCKET_WIDTH, MAX_BUCKET_SIZE),
      height: clamp(bottom - top, MIN_BUCKET_HEIGHT, MAX_BUCKET_SIZE),
    })
  }
  return moved
}

function round1(v) {
  return Math.round(v * 10) / 10
}
