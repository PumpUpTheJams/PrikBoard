/**
 * Links to post-its. A post-it's address is its board in the hash with the post-it opened up: /#/boards/{id}/notes/{id}
 * (NoteDetail.path). Links between post-its store that path without a host (NoteLinks on the server), so they keep
 * working wherever PrikBoard runs.
 */

const NOTE_HASH = /^#\/boards\/([0-9a-f-]{36})\/notes\/([0-9a-f-]{36})$/i
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export interface NoteRoute {
  boardId: string
  noteId: string
}

/** Suggested title, shown greyed in an empty title field. Never stored: a new post-it's title is empty. */
export const TITLE_SUGGESTION = 'Post-it title'

/** How a post-it is named where its title is text in a list or label: its title, or a stand-in when it has none. */
export const noteLabel = (note: { title: string }) => note.title || 'Untitled post-it'

export const notePath = ({ boardId, noteId }: NoteRoute) => `/#/boards/${boardId}/notes/${noteId}`

/** The post-it open in the address bar, if any. */
export function noteIdFromHash(): string | null {
  return window.location.hash.match(NOTE_HASH)?.[2] ?? null
}

const TRASH_HASH = /^#\/boards\/[0-9a-f-]{36}\/trash$/i

/** The open trash keeps its place in the address too (the board menu opens it through it): /#/boards/{id}/trash. */
export const trashPath = (boardId: string) => `/#/boards/${boardId}/trash`

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
  const match = hash.match(NOTE_HASH)
  return match ? { boardId: match[1], noteId: match[2] } : null
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
