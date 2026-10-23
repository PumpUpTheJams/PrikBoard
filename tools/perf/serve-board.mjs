#!/usr/bin/env node
// A read-only stand-in for the PrikBoard API that serves one board, so the web app can be measured without signing in
// (see README.md). It answers what opening a board needs: the caller, the board list, the board itself (a getBoard
// snapshot saved by the server benchmark), and its image and drawing content (taken from the make-board.mjs export).
// Everything else is a 404, and changes a 405: drags that drop show an error and change nothing.
//
//   node tools/perf/serve-board.mjs --snapshot=board.json --export=perf-board.json [--port=5099]
//
// Point the web app at it with PRIKBOARD_API_URL=http://localhost:5099.
import { readFileSync } from 'node:fs'
import { createServer } from 'node:http'

const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [key, value] = a.replace(/^--/, '').split('=')
    return [key, value]
  }),
)
if (!args.snapshot || !args.export) throw new Error('Give --snapshot (a getBoard response) and --export (the make-board.mjs file).')
const board = JSON.parse(readFileSync(args.snapshot, 'utf8'))
const exported = JSON.parse(readFileSync(args.export, 'utf8'))
const port = Number(args.port ?? 5099)

const me = board.people.find((p) => p.isYou) ?? board.people[0]
const caller = {
  kind: 'Session',
  principalKind: 'User',
  id: me.principalId,
  displayName: me.name,
  chosenDisplayName: me.name,
  suggestedDisplayName: null,
  provider: null,
  clientId: null,
  identities: [],
  scopes: [],
  isAdmin: false,
  avatarUrl: null,
}
const summary = (({ cards, buckets, images, drawings, tags, people, ...rest }) => rest)(board)

// Content by the order the snapshot lists items in; any image or drawing of the export will do for measuring.
const imageBytes = exported.images.map((i) => Buffer.from(i.data, 'base64'))
const imageIndex = new Map(board.images.map((image, i) => [image.id, i % Math.max(1, imageBytes.length)]))
const drawingIndex = new Map(board.drawings.map((drawing, i) => [drawing.id, i % Math.max(1, exported.drawings.length)]))

const send = (res, status, body, type = 'application/json') => {
  res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store' })
  res.end(type === 'application/json' ? JSON.stringify(body) : body)
}

createServer((req, res) => {
  const path = new URL(req.url, 'http://localhost').pathname
  if (req.method !== 'GET' && req.method !== 'HEAD') return send(res, 405, { title: 'This stand-in API is read-only.' })
  if (path === '/api/me') return send(res, 200, caller)
  if (path === '/api/setup') return send(res, 200, { needed: false, recovery: false, unlocked: false })
  if (path === '/api/auth/version') return send(res, 200, { version: 'perf' })
  if (path === '/api/boards') return send(res, 200, [summary])
  if (path === `/api/boards/${board.id}` || path === `/api/boards/${board.shortId}`) return send(res, 200, board)
  let match
  if ((match = path.match(/^\/api\/images\/([^/]+)\/content$/)) && imageIndex.has(match[1])) {
    return send(res, 200, imageBytes[imageIndex.get(match[1])], 'image/png')
  }
  if ((match = path.match(/^\/api\/drawings\/([^/]+)\/content$/)) && drawingIndex.has(match[1])) {
    const strokes = exported.drawings[drawingIndex.get(match[1])].strokes.map(({ strokeWidth, points }) => ({ strokeWidth, points }))
    return send(res, 200, { drawingId: match[1], strokes })
  }
  send(res, 404, { title: 'Not in this stand-in API.' })
}).listen(port, () => console.log(`Serving "${board.name}" (#/boards/${board.shortId}) on http://localhost:${port}`))
