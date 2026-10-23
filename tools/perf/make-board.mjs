#!/usr/bin/env node
// Writes a big board as a PrikBoard export file, for measuring how the app copes (see README.md). Import it through
// the board menu's Import, or POST it to /api/boards/import. No dependencies: run it with plain Node.
//
//   node tools/perf/make-board.mjs [--cards=1250] [--images=100] [--drawings=100] [--out=perf-board.json]
//
// The layout is the same every time (only ids differ): six buckets side by side holding 60% of the cards, the largest
// one 30% of those, an empty bucket to drop groups into, the other cards loose below, and images and drawings to the
// left of the buckets.
import { randomUUID } from 'node:crypto'
import { writeFileSync } from 'node:fs'
import { deflateSync } from 'node:zlib'

/** Mirrors BoardExport.CurrentVersion (src/PrikBoard.Api/Contracts/BoardExport.cs). */
const VERSION = 8
const MAX_CARDS = 2000
const MAX_IMAGES = 200
/** The import takes at most 64 MB (BoardTransferEndpoints.MaxImportBytes); stay well under it. */
const MAX_FILE_BYTES = 60 * 1024 * 1024

const GRID = 20
const CARD = { width: 200, height: 120 }
const COLUMN_STEP = CARD.width + GRID
const ROW_STEP = CARD.height + GRID
/** Rows that fit in a bucket at Layout.MaxBucketSize (10,000 px). */
const MAX_ROWS = Math.floor((10_000 - GRID) / ROW_STEP)

const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [key, value] = a.replace(/^--/, '').split('=')
    return [key, value ?? 'true']
  }),
)
const count = (name, fallback, max) => {
  const n = Number(args[name] ?? fallback)
  if (!Number.isInteger(n) || n < 0 || n > max) throw new Error(`--${name} must be a whole number from 0 to ${max}`)
  return n
}
const cards = count('cards', 1250, MAX_CARDS)
const images = count('images', 100, MAX_IMAGES)
const drawings = count('drawings', 100, 5000)
const out = args.out ?? 'perf-board.json'

/** A small deterministic random number generator, so every run lays out the same board. */
let seed = 42
const random = () => {
  seed = (seed * 1664525 + 1013904223) % 2 ** 32
  return seed / 2 ** 32
}
const pick = (list) => list[Math.floor(random() * list.length)]

const fills = ['Butter', 'Mint', 'Sky', 'Coral', 'Lilac']
const tags = ['ui', 'api', 'bug', 'feature', 'docs', 'design', 'mobile', 'search', 'live', 'perf', 'auth', 'tweak']
const tagColors = { bug: 'Brick', feature: 'Moss', perf: 'Ochre', design: 'Violet', auth: 'Ocean' }
const words = 'the board card bucket drag drop image drawing tag person agent search filter zoom scroll pointer live server'.split(' ')
const sentence = (n) => Array.from({ length: n }, () => pick(words)).join(' ')

function card(i, x, y) {
  const components = [{ kind: 'Text', text: `${sentence(12)}.\n\n${sentence(30)}.` }]
  if (i % 4 === 0) {
    components.push({ kind: 'Checklist', title: 'Steps', items: Array.from({ length: 5 }, (_, j) => ({ text: sentence(4), done: j < 2 })) })
  }
  const cardTags = [...new Set(Array.from({ length: i % 4 }, () => pick(tags)))]
  return { id: randomUUID(), title: `${sentence(5)} ${i + 1}`, color: pick(fills), tags: cardTags, x, y, components }
}

// Six buckets side by side, four card columns each, holding 60% of the cards.
const COLUMNS = 4
const BUCKET_WIDTH = GRID + COLUMNS * COLUMN_STEP
const shares = [0.3, 0.2, 0.2, 0.1, 0.1, 0.1]
const inBuckets = Math.round(cards * 0.6)
const buckets = []
const exportedCards = []
let made = 0
shares.forEach((share, b) => {
  const x = b * (BUCKET_WIDTH + 2 * GRID)
  const n = Math.min(Math.round(inBuckets * share), MAX_ROWS * COLUMNS, cards - made)
  const rows = Math.max(1, Math.ceil(n / COLUMNS))
  buckets.push({ name: `Bucket ${b + 1} (${n})`, x, y: 0, width: BUCKET_WIDTH, height: GRID + rows * ROW_STEP })
  for (let i = 0; i < n; i++) {
    exportedCards.push(card(made++, x + GRID + (i % COLUMNS) * COLUMN_STEP, GRID + Math.floor(i / COLUMNS) * ROW_STEP))
  }
})
const bucketed = made
// An empty bucket to drop groups of cards into.
const right = shares.length * (BUCKET_WIDTH + 2 * GRID)
buckets.push({ name: 'Drop here', x: right, y: 0, width: BUCKET_WIDTH, height: 600 })

// The rest loose, 40 to a row, below the buckets.
const below = Math.max(...buckets.map((b) => b.y + b.height)) + 400
for (let i = 0; made < cards; i++) exportedCards.push(card(made++, (i % 40) * COLUMN_STEP, below + Math.floor(i / 40) * ROW_STEP))

/** A PNG of `width` × `height`: a gradient with stripes, which compresses well but decodes to its full size. */
function png(width, height, hue) {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    return c >>> 0
  })
  const crc = (bytes) => {
    let c = 0xffffffff
    for (const b of bytes) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8)
    return (c ^ 0xffffffff) >>> 0
  }
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
    const length = Buffer.alloc(4)
    length.writeUInt32BE(data.length)
    const sum = Buffer.alloc(4)
    sum.writeUInt32BE(crc(body))
    return Buffer.concat([length, body, sum])
  }
  const header = Buffer.alloc(13)
  header.writeUInt32BE(width, 0)
  header.writeUInt32BE(height, 4)
  header[8] = 8 // bit depth
  header[9] = 2 // RGB
  const raw = Buffer.alloc((width * 3 + 1) * height)
  for (let y = 0; y < height; y++) {
    const row = y * (width * 3 + 1)
    raw[row] = 0
    // Rows repeat within a band of 40, so each one compresses to a reference to the row above.
    const band = Math.floor(y / 40)
    const stripe = band % 2 ? 30 : 0
    for (let x = 0; x < width; x++) {
      raw[row + 1 + x * 3] = (hue + (x * 255) / width + stripe) & 0xff
      raw[row + 2 + x * 3] = ((band * 40 * 255) / height + stripe) & 0xff
      raw[row + 3 + x * 3] = (255 - hue + stripe) & 0xff
    }
  }
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
  return Buffer.concat([signature, chunk('IHDR', header), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))])
}

// Images and drawings to the left of the buckets, ten to a row.
const exportedImages = Array.from({ length: images }, (_, i) => ({
  cardId: null,
  fileName: `photo-${i + 1}.png`,
  caption: i % 3 === 0 ? `Photo ${i + 1}` : null,
  x: -4000 + (i % 10) * 360,
  y: Math.floor(i / 10) * 280,
  width: 300,
  data: png(2400, 1600, (i * 37) % 256).toString('base64'),
}))
const imageRows = Math.ceil(images / 10)
const exportedDrawings = Array.from({ length: drawings }, (_, i) => ({
  cardId: null,
  x: -4000 + (i % 10) * 360,
  y: imageRows * 280 + 200 + Math.floor(i / 10) * 300,
  strokes: [
    {
      color: pick(['Chalk', 'Butter', 'Mint', 'Sky', 'Coral', 'Lilac']),
      strokeWidth: 4,
      points: Array.from({ length: 300 }, (_, p) => [
        Math.round((120 + 100 * Math.cos(p / 15 + i)) * 10) / 10,
        Math.round((120 + 100 * Math.sin(p / 11 + i)) * 10) / 10,
        // Pen pressure changes smoothly, as with a real pen.
        Math.round((0.5 + 0.2 * Math.sin(p / 25 + i)) * 100) / 100,
      ]),
    },
  ],
}))

const file = {
  format: 'prikboard-board',
  version: VERSION,
  exportedAt: new Date().toISOString(),
  name: `Perf ${cards}/${images}/${drawings}`,
  description: 'A generated board for measuring performance (tools/perf).',
  buckets,
  tags,
  tagColors,
  cards: exportedCards,
  images: exportedImages,
  drawings: exportedDrawings,
  key: 'PERF',
}
const json = JSON.stringify(file)
if (json.length > MAX_FILE_BYTES) throw new Error(`The file would be ${(json.length / 2 ** 20).toFixed(0)} MB, over the import limit. Use fewer --images.`)
writeFileSync(out, json)
console.log(`${out}: ${cards} cards (${bucketed} in buckets), ${images} images, ${drawings} drawings, ${(json.length / 2 ** 20).toFixed(1)} MB`)
