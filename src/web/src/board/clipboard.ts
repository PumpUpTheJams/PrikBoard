import type { BoardClip } from '../api/types'

// Copied cards go on the system clipboard as the clip itself (from copyItems), so they paste on any PrikBoard server, in
// any browser. It rides along in the HTML version, in an attribute, under a readable list of the cards: pasted into a
// document or chat, that list (or the plain text) is what shows. The board reads the attribute back on paste.

const ATTRIBUTE = 'data-prikboard-clip'

const escapeHtml = (text: string) =>
  text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

/** The card's readable id, title and link, for each card. */
function lines(clip: BoardClip) {
  const server = clip.source.url.replace(/\/+$/, '')
  return clip.cards.map((card) => ({
    id: `${clip.source.boardKey}-${card.number}`,
    title: card.title || 'Untitled card',
    url: `${server}/#/boards/${clip.source.boardShortId}/cards/${card.number}`,
  }))
}

function html(clip: BoardClip) {
  const items = lines(clip)
    .map((c) => `<li><a href="${escapeHtml(c.url)}">${escapeHtml(c.id)}</a> ${escapeHtml(c.title)}</li>`)
    .join('')
  return `<meta charset="utf-8"><div ${ATTRIBUTE}="${escapeHtml(JSON.stringify(clip))}"><ul>${items}</ul></div>`
}

const text = (clip: BoardClip) => lines(clip).map((c) => `${c.id} ${c.title}\n${c.url}`).join('\n')

/**
 * Puts the clip on the clipboard once it has loaded. Call it straight from the key press: the browser only allows the
 * write during the user's gesture, so it is started at once with the clip still on its way. Throws what went wrong.
 */
export async function writeClip(load: Promise<BoardClip>): Promise<void> {
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
    if (await written) return
    // Refused (some embedded browsers allow no clipboard writes): the copy command may still work.
    return copyCommand(clip)
  }
  return copyCommand(await load)
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
