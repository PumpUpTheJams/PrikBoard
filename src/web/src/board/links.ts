/**
 * Links to post-its. A post-it's address is its board in the hash with the post-it opened up:
 * /#/boards/{shortId}/notes/{number} (NoteDetail.path). The older form with both ids, /#/boards/{id}/notes/{id}, still
 * works. Links between post-its store the path without a host (NoteLinks on the server), so they keep working wherever
 * PrikBoard runs.
 */

/** A board in an address: its id, or (as written now) its short id. */
const BOARD_REF = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-hjkmnp-tv-z]{6}'
/** A post-it in an address: its id, or (as written now) its number. */
const NOTE_REF = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[1-9][0-9]*'
const NOTE_HASH = new RegExp(`^#/boards/(${BOARD_REF})/notes/(${NOTE_REF})$`, 'i')
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Where a link to a post-it points: its board and the post-it, by id or by short id and number, in lower case. */
export interface NoteRoute {
  board: string
  note: string
}

/** Suggested title, shown greyed in an empty title field. Never stored: a new post-it's title is empty. */
export const TITLE_SUGGESTION = 'Post-it title'

/** How a post-it is named where its title is text in a list or label: its title, or a stand-in when it has none. */
export const noteLabel = (note: { title: string }) => note.title || 'Untitled post-it'

export const notePath = ({ board, note }: NoteRoute) => `/#/boards/${board}/notes/${note}`

/** The board in the address bar, by id or short id, if any. */
export const boardRefFromHash = () => window.location.hash.match(/^#\/boards\/([\w-]+)/)?.[1]?.toLowerCase() ?? null

/** Whether `ref` (from an address) names `board`. */
export const isBoard = (board: { id: string; shortId: string }, ref: string | null) => ref === board.id || ref === board.shortId

/** The post-it open in the address bar, if any. */
export function noteRouteFromHash(): NoteRoute | null {
  return routeOf(window.location.hash)
}

/** The post-it of `board` that the address bar opens, if any. */
export function noteInHash(board: { id: string; shortId: string; notes: { id: string; path: string }[] }): string | null {
  const route = noteRouteFromHash()
  return route && isBoard(board, route.board) ? (linkedNote(board.notes, notePath(route))?.id ?? null) : null
}

/** Whether `url` links to `note`, in either form of its path. */
export function linksTo(url: string, note: { id: string; path: string }) {
  const route = noteRoute(url)
  return route !== null && (route.note === note.id || notePath(route) === note.path)
}

/** The post-it among `notes` (those of one board) that `url` links to. */
export const linkedNote = <T extends { id: string; path: string }>(notes: T[], url: string) => notes.find((n) => linksTo(url, n))

const TRASH_HASH = new RegExp(`^#/boards/(?:${BOARD_REF})/trash$`, 'i')

/** The open trash keeps its place in the address too (the board menu opens it through it): /#/boards/{shortId}/trash. */
export const trashPath = (boardRef: string) => `/#/boards/${boardRef}/trash`

/** Whether the address has the trash open. */
export const trashInHash = () => TRASH_HASH.test(window.location.hash)

/** The post-it a link points at: a post-it's path, or its full address on this site. Null for anything else. */
export function noteRoute(url: string): NoteRoute | null {
  let hash: string
  if (url.startsWith('/#')) {
    hash = url.slice(1)
  } else {
    try {
      const parsed = new URL(url)
      if (parsed.origin !== window.location.origin || parsed.pathname !== '/') return null
      hash = parsed.hash
    } catch {
      return null
    }
  }
  return routeOf(hash)
}

function routeOf(hash: string): NoteRoute | null {
  const match = hash.match(NOTE_HASH)
  return match ? { board: match[1].toLowerCase(), note: match[2].toLowerCase() } : null
}

export const isNoteId = (value: string) => ID.test(value)

/** Puts text on the clipboard. Falls back to a hidden text area where the Clipboard API is missing or refused. */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    const area = document.createElement('textarea')
    area.value = text
    area.style.position = 'fixed'
    area.style.opacity = '0'
    document.body.append(area)
    area.select()
    try {
      return document.execCommand('copy')
    } catch {
      return false
    } finally {
      area.remove()
    }
  }
}
