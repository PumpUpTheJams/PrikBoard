import type { BoardClip } from '../api/types'

// Copied items go on the system clipboard as the clip itself (from copyItems), so they paste on any PrikBoard server, in
// any browser. It rides along in the HTML version, in an attribute, under a readable list of what is in it: pasted into
// a document or chat, that list (or the plain text) is what shows. The board reads the attribute back on paste.

const ATTRIBUTE = 'data-prikboard-clip'

const escapeHtml = (text: string) =>
  text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

/** "1 bucket, 3 cards and 2 images" from counts and nouns; the ones at 0 are left out. */
export function countList(parts: readonly (readonly [number, string])[]) {
  const named = parts.filter(([n]) => n > 0).map(([n, one]) => (n === 1 ? `1 ${one}` : `${n} ${one}s`))
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

function html(clip: BoardClip) {
  const items = [
    ...others(clip).map((line) => `<li>${escapeHtml(line)}</li>`),
    ...lines(clip).map((c) => `<li><a href="${escapeHtml(c.url)}">${escapeHtml(c.id)}</a> ${escapeHtml(c.title)}</li>`),
  ].join('')
  return `<meta charset="utf-8"><div ${ATTRIBUTE}="${escapeHtml(JSON.stringify(clip))}"><ul>${items}</ul></div>`
}

const text = (clip: BoardClip) => [...others(clip), ...lines(clip).map((c) => `${c.id} ${c.title}\n${c.url}`)].join('\n')

/**
 * Puts the clip on the clipboard once it has loaded, and returns it. Call it straight from the key press: the browser
 * only allows the write during the user's gesture, so it is started at once with the clip still on its way. Throws what
 * went wrong.
 */
export async function writeClip(load: Promise<BoardClip>): Promise<BoardClip> {
  if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
    const written = navigator.clipboard
      .write([
        new ClipboardItem({
          'text/html': load.then((clip) => new Blob([html(clip)], { type: 'text/html' })),
          'text/plain': load.then((clip) => new Blob([text(clip)], { type: 'text/plain' })),
        }),
      ])
      .then(
        () => true,
        () => false,
      )
    // A failed load says more than the refused write it causes.
    const clip = await load
    // Refused (some embedded browsers allow no clipboard writes): the copy command may still work.
    if (!(await written)) copyCommand(clip)
    return clip
  }
  const clip = await load
  copyCommand(clip)
  return clip
}

/** Copies with the copy command, which browsers still allow shortly after the key press. */
function copyCommand(clip: BoardClip) {
  const onCopy = (e: ClipboardEvent) => {
    e.preventDefault()
    e.clipboardData?.setData('text/html', html(clip))
    e.clipboardData?.setData('text/plain', text(clip))
  }
  document.addEventListener('copy', onCopy)
  try {
    if (!document.execCommand('copy')) throw new Error("The browser didn't allow copying.")
  } finally {
    document.removeEventListener('copy', onCopy)
  }
}

/** The clip in pasted data, or null when it holds none (or isn't one this app understands). */
export function readClip(data: DataTransfer | null): BoardClip | null {
  const markup = data?.getData('text/html')
  if (!markup?.includes(ATTRIBUTE)) return null
  const value = new DOMParser().parseFromString(markup, 'text/html').querySelector(`[${ATTRIBUTE}]`)?.getAttribute(ATTRIBUTE)
  if (!value) return null
  try {
    const clip = JSON.parse(value) as BoardClip
    return clip?.format === 'prikboard-clip' && Array.isArray(clip.cards) ? clip : null
  } catch {
    return null
  }
}
