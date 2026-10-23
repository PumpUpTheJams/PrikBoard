/**
 * Links to cards, shared by the web app and the VS Code extension (so no DOM here). A card's address is its board in
 * the hash with the card opened up: /#/boards/{shortId}/cards/{number} (CardDetail.path). The older form with both
 * ids, /#/boards/{id}/cards/{id}, still works. Links between cards store the path without a host (CardLinks on the
 * server), so they keep working wherever PrikBoard runs.
 */

/** A board in an address: its id, or (as written now) its short id. */
export const BOARD_REF = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-hjkmnp-tv-z]{6}'
/** A card in an address: its id, or (as written now) its number. */
const CARD_REF = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[1-9][0-9]*'
const CARD_HASH = new RegExp(`^#/boards/(${BOARD_REF})/cards/(${CARD_REF})$`, 'i')

/** Where a link to a card points: its board and the card, by id or by short id and number, in lower case. */
export interface CardRoute {
  board: string
  card: string
}

export const cardPath = ({ board, card }: CardRoute) => `/#/boards/${board}/cards/${card}`

/** The card a hash (`#/boards/...`) opens, if any. */
export function routeOf(hash: string): CardRoute | null {
  const match = hash.match(CARD_HASH)
  return match ? { board: match[1].toLowerCase(), card: match[2].toLowerCase() } : null
}

/**
 * The card a link points at: a card's path, or its full address on the PrikBoard site at `origin` (e.g.
 * `https://prikboard.example`). Null for anything else.
 */
export function cardRoute(url: string, origin: string): CardRoute | null {
  let hash: string
  if (url.startsWith('/#')) {
    hash = url.slice(1)
  } else {
    try {
      const parsed = new URL(url)
      if (parsed.origin !== origin || parsed.pathname !== '/') return null
      hash = parsed.hash
    } catch {
      return null
    }
  }
  return routeOf(hash)
}
