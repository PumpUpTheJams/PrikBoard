import type { BoardClip } from '../api/types'

// Copied items go on the system clipboard as the clip itself (from copyItems), so they paste on any PrikBoard server, in
// any browser. It rides along in the HTML version, in an attribute, under a readable list of what is in it: pasted into
// a document or chat, that list (or the plain text) is what shows. The board reads the attribute back on paste. A big
// clip (a whole board, say) goes in a type of its own instead, which only browsers hand back, so documents and chats it
// is pasted into don't have to read it all.

const ATTRIBUTE = 'data-prikboard-clip'

/** The clipboard type a big clip goes in. */
const CLIP_TYPE = 'application/x-prikboard-clip'

/** A clip longer than this (in characters of JSON) goes in CLIP_TYPE rather than in the HTML. */
const BIG_CLIP_CHARS = 4 * 1024 * 1024

const escapeHtml = (text: string) =>
  text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

/** For an attribute in single quotes: JSON has few of those, so it stays about as long as it is. */
const escapeAttribute = (text: string) => text.replace(/&/g, '&amp;').replace(/'/g, '&#39;')

const unescapeAttribute = (text: string) => text.replace(/&(amp|#39);/g, (_, entity: string) => (entity === 'amp' ? '&' : "'"))

/** "1 bucket, 3 cards and 2 images" from counts and nouns; the ones at 0 are left out. */
export function countList(parts: readonly (readonly [number, string])[]) {
  const named = parts.filter(([n]) => n > 0).map(([n, one]) => (n === 1 ? `1 ${one}` : `${n.toLocaleString()} ${one}s`))
  return named.length > 1 ? `${named.slice(0, -1).join(', ')} and ${named[named.length - 1]}` : (named[0] ?? 'nothing')
}

/** What a clip holds, for a toast: its buckets, cards, and images and drawings on the board (not those on the cards). */
export const clipSummary = (clip: BoardClip) =>
  countList([
    [clip.buckets?.length ?? 0, 'bucket'],
    [clip.cards.length, 'card'],
    [clip.images.filter((i) => !i.cardId).length, 'image'],
    [clip.drawings.filter((d) => !d.cardId).length, 'drawing'],
  ])

/** The cards' readable ids, titles and links. */
function lines(clip: BoardClip) {
  const server = clip.source.url.replace(/\/+$/, '')
  return clip.cards.map((card) => ({
    id: `${clip.source.boardKey}-${card.number}`,
    title: card.title || 'Untitled card',
    url: `${server}/#/boards/${clip.source.boardShortId}/cards/${card.number}`,
  }))
}

/** Buckets by name, and a count of the images and drawings, for the readable versions. */
function others(clip: BoardClip) {
  const buckets = (clip.buckets ?? []).map((b) => `Bucket: ${b.name}`)
  const loose = countList([
    [clip.images.filter((i) => !i.cardId).length, 'image'],
    [clip.drawings.filter((d) => !d.cardId).length, 'drawing'],
  ])
  return loose === 'nothing' ? buckets : [...buckets, loose]
}

/** The readable list, with the clip itself in its attribute when `json` is given. */
function html(clip: BoardClip, json?: string) {
  const items = [
    ...others(clip).map((line) => `<li>${escapeHtml(line)}</li>`),
    ...lines(clip).map((c) => `<li><a href="${escapeHtml(c.url)}">${escapeHtml(c.id)}</a> ${escapeHtml(c.title)}</li>`),
  ].join('')
  return json === undefined
    ? `<meta charset="utf-8"><ul>${items}</ul>`
    : `<meta charset="utf-8"><div ${ATTRIBUTE}='${escapeAttribute(json)}'><ul>${items}</ul></div>`
}

const text = (clip: BoardClip) => [...others(clip), ...lines(clip).map((c) => `${c.id} ${c.title}\n${c.url}`)].join('\n')

/** What writeClip did: the clip, and whether it is on the clipboard now (else `copyCommand` may put it there later). */
export interface WrittenClip {
  clip: BoardClip
  written: boolean
}

/**
 * Puts the clip on the clipboard once it has loaded. Call it straight from the key press: the browser only allows the
 * write during the user's gesture, so it is started at once with the clip still on its way. A big clip, or one that
 * took too long, may not get there (`written` false): `copyCommand` on a later key press or click can put it there.
 * Throws what went wrong while loading; the clipboard is left as it was then.
 */
export async function writeClip(load: Promise<BoardClip>): Promise<WrittenClip> {
  if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
    let json = ''
    // The async clipboard can't write CLIP_TYPE: a big clip fails here and goes through the copy command.
    const small = load.then((clip) => {
      json = JSON.stringify(clip)
      if (json.length > BIG_CLIP_CHARS) throw new Error('A big clip goes in a type of its own.')
      return clip
    })
    const markup = small.then((clip) => new Blob([html(clip, json)], { type: 'text/html' }))
    const plain = small.then((clip) => new Blob([text(clip)], { type: 'text/plain' }))
    // A refused write may never read them: their failure is the write's, not one to report.
    for (const promise of [markup, plain]) promise.catch(() => {})
    const written = navigator.clipboard
      .write([new ClipboardItem({ 'text/html': markup, 'text/plain': plain })])
      .then(
        () => true,
        () => false,
      )
    // A failed load says more than the refused write it causes.
    const clip = await load
    // Refused (some embedded browsers allow no clipboard writes): the copy command may still work.
    return { clip, written: (await written) || copyCommand(clip, json || undefined) }
  }
  const clip = await load
  return { clip, written: copyCommand(clip) }
}

/**
 * Copies with the copy command, which browsers allow during a key press or click and shortly after; false when it
 * was refused. A big clip goes in CLIP_TYPE, with only the readable list in the HTML.
 */
export function copyCommand(clip: BoardClip, json = JSON.stringify(clip)) {
  const big = json.length > BIG_CLIP_CHARS
  const onCopy = (e: ClipboardEvent) => {
    e.preventDefault()
    if (big) e.clipboardData?.setData(CLIP_TYPE, json)
    e.clipboardData?.setData('text/html', html(clip, big ? undefined : json))
    e.clipboardData?.setData('text/plain', text(clip))
  }
  document.addEventListener('copy', onCopy)
  try {
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    document.removeEventListener('copy', onCopy)
  }
}

/** The clip's JSON in pasted HTML, or null without one. */
function jsonInHtml(markup: string | undefined) {
  if (!markup?.includes(ATTRIBUTE)) return null
  const quoted = markup.indexOf(`${ATTRIBUTE}='`)
  if (quoted >= 0) {
    const start = quoted + ATTRIBUTE.length + 2
    const end = markup.indexOf("'", start)
    if (end > start) return unescapeAttribute(markup.slice(start, end))
  }
  // In double quotes (written by an older version, or again by the browser): read it as HTML.
  return new DOMParser().parseFromString(markup, 'text/html').querySelector(`[${ATTRIBUTE}]`)?.getAttribute(ATTRIBUTE) ?? null
}

/** A clip read from the clipboard, and how long its JSON is. */
export interface PastedClip {
  clip: BoardClip
  size: number
}

/** The clip in pasted data, or null when it holds none (or isn't one this app understands). */
export function readClip(data: DataTransfer | null): PastedClip | null {
  const json = data?.getData(CLIP_TYPE) || jsonInHtml(data?.getData('text/html'))
  if (!json) return null
  try {
    const clip = JSON.parse(json) as BoardClip
    return clip?.format === 'prikboard-clip' && Array.isArray(clip.cards) ? { clip, size: json.length } : null
  } catch {
    return null
  }
}
