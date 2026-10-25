import { memo, useState, type KeyboardEvent } from 'react'
import type { BucketDetail, CardDetail } from '../api/types'
import { cardLabel } from './links'

interface CardIndexProps {
  /** The cards on the board, in board order (bucket by bucket, loose cards last). */
  cards: CardDetail[]
  buckets: BucketDetail[]
  /** A card in the list got focus: select it and bring it on screen. */
  onFocusCard: (card: CardDetail) => void
  /** Opens a card, as Enter or a double-click does on the board. */
  onOpenCard: (id: string) => void
}

/**
 * Every card on the board as a list for keyboard and screen-reader users, unseen. The board itself only puts the cards
 * near the screen on the page, so this is how the others can still be reached: Tab goes into the list once, the arrow
 * keys (and Home and End) move through it, and each card that gets focus is selected and scrolled to, where Delete and
 * the board's other keys work on it as usual. Enter opens it.
 */
export const CardIndex = memo(function CardIndex({ cards, buckets, onFocusCard, onOpenCard }: CardIndexProps) {
  /** The one card in the list that Tab reaches; the arrow keys move it. */
  const [active, setActive] = useState(0)
  const bucketNames = new Map(buckets.map((b) => [b.id, b.name]))

  function onKeyDown(e: KeyboardEvent<HTMLUListElement>) {
    const buttons = [...e.currentTarget.querySelectorAll<HTMLButtonElement>('button')]
    const at = buttons.indexOf(document.activeElement as HTMLButtonElement)
    const next = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: buttons.length - 1 }[e.key]
    if (next === undefined || at < 0) return
    e.preventDefault()
    // Not to the board: the arrow keys move through the list.
    e.stopPropagation()
    buttons[Math.min(buttons.length - 1, Math.max(0, next))]?.focus()
  }

  return (
    <ul className="visually-hidden" aria-label="Cards on this board" onKeyDown={onKeyDown}>
      {cards.map((card, i) => {
        const bucket = card.bucketId ? bucketNames.get(card.bucketId) : undefined
        return (
          <li key={card.id}>
            <button
              type="button"
              tabIndex={i === Math.min(active, cards.length - 1) ? 0 : -1}
              aria-label={`${cardLabel(card)}, ${card.readableId}, ${bucket ? `in ${bucket}` : 'loose on the board'}`}
              onFocus={() => {
                setActive(i)
                onFocusCard(card)
              }}
              onClick={() => onOpenCard(card.id)}
            >
              {cardLabel(card)}
            </button>
          </li>
        )
      })}
    </ul>
  )
})
