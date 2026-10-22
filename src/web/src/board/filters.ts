import type { CardDetail } from '../api/types'

/** Stands for "no one" among the people picked in the assignee filter: cards without assignees. */
export const NOBODY = 'nobody'

/** Whether a card has any of the picked people on it (or none at all, when NOBODY is picked). */
export const assignedToAny = (card: CardDetail, ids: string[]) =>
  card.assignees.some((a) => ids.includes(a.principalId)) || (card.assignees.length === 0 && ids.includes(NOBODY))
