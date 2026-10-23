import { useState } from 'react'
import type { BoardDetail, CardDetail } from '../api/types'
import { assignedToAny } from './filters'

/** The tag and people filters at the bottom left of the board: cards that don't pass fade. Not kept: every board starts unfiltered. */
export function useBoardFilters(board: BoardDetail) {
  const [tagFilter, setTagFilter] = useState<string[]>([])
  /** Principal ids, or NOBODY for cards without assignees. */
  const [assigneeFilter, setAssigneeFilter] = useState<string[]>([])
  // Filter on tags that still exist (one may have been renamed or deleted meanwhile); any of them matches.
  const tags = tagFilter.filter((t) => board.tags.some((bt) => bt.name === t))
  const tagFaded = (card: CardDetail) => tags.length > 0 && !card.tags.some((t) => tags.includes(t))
  const assigneeFaded = (card: CardDetail) => assigneeFilter.length > 0 && !assignedToAny(card, assigneeFilter)
  return {
    tags,
    setTags: setTagFilter,
    assignees: assigneeFilter,
    setAssignees: setAssigneeFilter,
    tagFaded,
    assigneeFaded,
    /** With both filters on, a card stays bright when it passes each. */
    faded: (card: CardDetail) => tagFaded(card) || assigneeFaded(card),
    clear() {
      setTagFilter([])
      setAssigneeFilter([])
    },
  }
}

export type BoardFiltersState = ReturnType<typeof useBoardFilters>
