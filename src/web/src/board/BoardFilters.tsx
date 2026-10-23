import type { BoardDetail } from '../api/types'
import { AssigneeFilter } from './AssigneeFilter'
import type { TagColors } from './tagColors'
import { TagFilter } from './TagFilter'
import type { BoardFiltersState } from './useBoardFilters'

/** The tag and people filters at the bottom left of the board. */
export function BoardFilters({ board, filters, tagColors }: { board: BoardDetail; filters: BoardFiltersState; tagColors: TagColors }) {
  return (
    <div className="board-filters">
      <TagFilter
        boardTags={board.tags}
        tagColors={tagColors}
        value={filters.tags}
        onChange={filters.setTags}
        matching={board.cards.filter((n) => !filters.tagFaded(n)).length}
        total={board.cards.length}
      />
      <AssigneeFilter
        people={board.people}
        cards={board.cards}
        value={filters.assignees}
        onChange={filters.setAssignees}
        matching={board.cards.filter((n) => !filters.assigneeFaded(n)).length}
        total={board.cards.length}
      />
    </div>
  )
}
