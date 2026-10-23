import type { CardDetail } from '../api/types'
import { CARD_WIDTH } from './layout'

/** Rough width of a tag chip: 11 px mono is about 6.6 px per character, plus padding and gap. */
const tagWidth = (tag: string) => tag.length * 6.6 + 16

/**
 * Title lines that fit on a card on the board (Card, and FarCard zoomed far out): three, or two when the tags wrap to
 * a second row. `people`: the assignees' avatars share the tags' row (not on the far face, which leaves them out).
 */
export function titleLines(card: CardDetail, people = true) {
  const avatars = people ? Math.min(card.assignees.length, 3) : 0
  const room = CARD_WIDTH - 24 - (avatars > 0 ? 24 + (avatars - 1) * 18 + 8 : 0)
  return card.tags.reduce((sum, tag) => sum + tagWidth(tag), 0) > room ? 2 : 3
}
