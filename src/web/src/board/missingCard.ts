import { api } from '../api/client'
import type { BoardDetail, CardDetail, ImageDetail } from '../api/types'
import { cardLabel, cardPath, linkedCard, type CardRoute } from './links'

/** Why a card link opened nothing, and the card itself when it is in the trash. */
export interface MissingCard {
  text: string
  /** The card in the trash, with its images. */
  deleted?: { card: CardDetail; images: ImageDetail[] }
}

const GONE = "That card doesn't exist anymore"

/**
 * Works out why the card `route` names isn't on `board`: deleted (still in the trash, members only) or gone for good.
 * A failed lookup says it's gone too.
 */
export async function describeMissingCard(board: BoardDetail, route: CardRoute): Promise<MissingCard> {
  if (board.visiting) return { text: GONE }
  try {
    const trash = await api.listDeletedCards(board.id)
    const card = linkedCard(trash.cards, cardPath(route))
    if (!card) return { text: GONE }
    return {
      text: `${card.readableId} ${cardLabel(card)} was deleted`,
      deleted: { card, images: trash.images.filter((i) => i.cardId === card.id) },
    }
  } catch {
    return { text: GONE }
  }
}
