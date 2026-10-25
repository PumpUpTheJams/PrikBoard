/**
 * Links to cards in the web app; the parsing itself is in api/cardLinks.ts, which the VS Code extension shares.
 */
import { BOARD_REF, cardPath, cardRoute as routeIn, routeOf, type CardRoute } from '../api/cardLinks'

export { cardPath, type CardRoute }

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Suggested title, shown grayed out in an empty title field. Never stored: a new card's title is empty. */
export const TITLE_SUGGESTION = 'Card title'

/** How a card is named where its title is text in a list or label: its title, or a stand-in when it has none. */
export const cardLabel = (card: { title: string }) => card.title || 'Untitled card'

/** The board in the address bar, by id or short id, if any. */
export const boardRefFromHash = () => window.location.hash.match(/^#\/boards\/([\w-]+)/)?.[1]?.toLowerCase() ?? null

/** Whether `ref` (from an address) names `board`. */
export const isBoard = (board: { id: string; shortId: string }, ref: string | null) => ref === board.id || ref === board.shortId

/** The card open in the address bar, if any. */
export function cardRouteFromHash(): CardRoute | null {
  return routeOf(window.location.hash)
}

/** The card of `board` that the address bar opens, if any. */
export function cardInHash(board: { id: string; shortId: string; cards: { id: string; path: string }[] }): string | null {
  const route = cardRouteFromHash()
  return route && isBoard(board, route.board) ? (linkedCard(board.cards, cardPath(route))?.id ?? null) : null
}

/** The card the address bar names on `board` when `board` doesn't have it (deleted, or never there), if any. */
export function missingCardInHash(board: { id: string; shortId: string; cards: { id: string; path: string }[] }): CardRoute | null {
  const route = cardRouteFromHash()
  return route && isBoard(board, route.board) && !linkedCard(board.cards, cardPath(route)) ? route : null
}

/** Whether `url` links to `card`, in either form of its path. */
export function linksTo(url: string, card: { id: string; path: string }) {
  const route = cardRoute(url)
  return route !== null && (route.card === card.id || cardPath(route) === card.path)
}

/** The card among `cards` (those of one board) that `url` links to. */
export const linkedCard = <T extends { id: string; path: string }>(cards: T[], url: string) => cards.find((n) => linksTo(url, n))

const TRASH_HASH = new RegExp(`^#/boards/(?:${BOARD_REF})/trash$`, 'i')

/** The open trash is in the address too (the board menu opens it by setting it): /#/boards/{shortId}/trash. */
export const trashPath = (boardRef: string) => `/#/boards/${boardRef}/trash`

/** Whether the address has the trash open. */
export const trashInHash = () => TRASH_HASH.test(window.location.hash)

/**
 * The mobile layout shows one bucket's cards at a time, and an image on its own; both keep their place in the address,
 * so the back button and a reload return there: /#/boards/{shortId}/buckets/{bucketId} (`loose` for the cards in no
 * bucket) and /#/boards/{shortId}/images/{imageId}. The desktop shows the board for either.
 */
const BUCKET_HASH = new RegExp(`^#/boards/(?:${BOARD_REF})/buckets/([0-9a-f-]{36}|loose)$`, 'i')
const IMAGE_HASH = new RegExp(`^#/boards/(?:${BOARD_REF})/images/([0-9a-f-]{36})$`, 'i')

/** What stands for "in no bucket" where a bucket id goes in an address. */
export const LOOSE = 'loose'

export const bucketPath = (boardRef: string, bucketId: string | null) => `/#/boards/${boardRef}/buckets/${bucketId ?? LOOSE}`
export const imagePath = (boardRef: string, imageId: string) => `/#/boards/${boardRef}/images/${imageId}`

/** The bucket open in the address (LOOSE for the cards in none), if any. */
export const bucketInHash = () => window.location.hash.match(BUCKET_HASH)?.[1]?.toLowerCase() ?? null

/** The image open in the address, if any. */
export const imageInHash = () => window.location.hash.match(IMAGE_HASH)?.[1]?.toLowerCase() ?? null

/** The card a link points at: a card's path, or its full address on this site. Null for anything else. */
export const cardRoute = (url: string): CardRoute | null => routeIn(url, window.location.origin)

export const isCardId = (value: string) => ID.test(value)

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
