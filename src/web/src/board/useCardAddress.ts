import { useEffect, useRef, useState } from 'react'
import type { BoardDetail } from '../api/types'
import { boardRefFromHash, cardInHash, cardLabel, isBoard, missingCardInHash, trashInHash, trashPath, type CardRoute } from './links'

interface CardAddressOptions {
  /** Whether the trash window is open: the address says so when no card is. */
  trashOpen: boolean
  /** A link to this board was followed: to a card on it (null: none), and whether to the trash (members only). */
  onFollow: (cardId: string | null, trash: boolean) => void
  /** A link to a card this board doesn't have (deleted, or never there) was followed, or the board opened with one. */
  onMissing: (route: CardRoute) => void
  /** A card is about to open. */
  onExpand: () => void
  /** The open card closed. */
  onClose: () => void
}

/**
 * The open card, kept in the address bar so it can be bookmarked or shared. A link to a card opens it when the board
 * loads, and following one to another card on this board opens that (one on another board loads that board: App).
 */
export function useCardAddress(board: BoardDetail, { trashOpen, onFollow, onMissing, onExpand, onClose }: CardAddressOptions) {
  /** `fresh`: just added, with its title ready to type. */
  const [expanded, setExpanded] = useState<{ id: string; fresh: boolean } | null>(() => {
    const id = cardInHash(board)
    return id ? { id, fresh: false } : null
  })
  // The listener below reads these through a ref, so it is added once: one removed and added again while a hashchange
  // is being dispatched (App re-renders the board during it when the address had no board) would miss that change.
  const latest = useRef({ board, onFollow, onMissing })
  useEffect(() => {
    latest.current = { board, onFollow, onMissing }
  })
  // The board opened on a link to a card it doesn't have: said once, before the address drops it.
  const [missingAtStart] = useState(() => missingCardInHash(board))
  const toldAtStart = useRef(false)
  useEffect(() => {
    if (!missingAtStart || toldAtStart.current) return
    toldAtStart.current = true
    latest.current.onMissing(missingAtStart)
  }, [missingAtStart])
  const openCard = expanded ? board.cards.find((n) => n.id === expanded.id) : undefined

  // Replacing the entry doesn't fire hashchange; a card that isn't (or no longer is) on the board drops out of the
  // address. An address in the older form (ids) is rewritten to the short one the same way.
  const openPath = openCard?.path ?? null
  useEffect(() => {
    const hash = openPath ? openPath.slice(1) : trashOpen ? trashPath(board.shortId).slice(1) : `#/boards/${board.shortId}`
    if (window.location.hash !== hash) history.replaceState(history.state, '', hash)
  }, [openPath, trashOpen, board.shortId])

  // The browser tab names what you are looking at: the open card (PRIK-118 Its title), or else the board. Away from
  // the board (admin page, sad page, signed out) it says PrikBoard again.
  const tabTitle = openCard ? `${openCard.readableId} ${cardLabel(openCard)}` : board.name
  useEffect(() => {
    document.title = tabTitle
  }, [tabTitle])
  useEffect(() => () => void (document.title = 'PrikBoard'), [])

  useEffect(() => {
    function onHashChange() {
      const { board, onFollow, onMissing } = latest.current
      if (!isBoard(board, boardRefFromHash())) return
      const id = cardInHash(board)
      setExpanded(id ? { id, fresh: false } : null)
      // The trash is for members; visitors of a public board don't see it.
      onFollow(id, trashInHash() && !board.visiting)
      const missing = id ? null : missingCardInHash(board)
      if (!missing) return
      // The address drops it at once (the effect above may not run again), so following the same link again says so again.
      history.replaceState(history.state, '', `#/boards/${board.shortId}`)
      onMissing(missing)
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  return {
    expanded,
    setExpanded,
    /** The open card, when it is on the board. */
    openCard,
    expand(id: string) {
      onExpand()
      setExpanded({ id, fresh: false })
    },
    /** Closes the open card. */
    close() {
      setExpanded(null)
      onClose()
    },
  }
}
