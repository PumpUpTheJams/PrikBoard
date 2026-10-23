import type { BoardTag } from '../api/types'
import { FilterPill } from './FilterPill'
import { FilterIcon } from './icons'
import { tagColorClass, type TagColors } from './tagColors'
import { normalizeTag, toggleIn } from './tags'

interface TagFilterProps {
  boardTags: BoardTag[]
  tagColors: TagColors
  /** The picked tags; cards with any of them stay bright, the others fade. */
  value: string[]
  onChange: (tags: string[]) => void
  /** Cards that match, of all of them. */
  matching: number
  total: number
}

/** A floating pill at the bottom left of the board (with the assignee filter) that filters cards by tag. */
export function TagFilter({ boardTags, tagColors, value, onChange, matching, total }: TagFilterProps) {
  if (boardTags.length === 0 && value.length === 0) return null

  const toggle = (name: string) => onChange(toggleIn(value, name))

  return (
    <FilterPill
      icon={<FilterIcon />}
      label="Filter by tag"
      placeholder="Find a tag…"
      chips={value.map((tag) => ({
        key: tag,
        label: tag,
        className: `tag-filter-chip${tagColorClass(tagColors, tag)}`,
        onRemove: () => toggle(tag),
      }))}
      onClear={() => onChange([])}
      rows={(query) => {
        const wanted = normalizeTag(query)
        return boardTags
          .filter((t) => t.name.includes(wanted))
          .map((t) => ({
            key: t.id,
            label: t.name,
            meta: t.cardCount,
            checked: value.includes(t.name),
            onSelect: () => toggle(t.name),
            trailing: (
              <span className="picker-color" aria-hidden="true">
                <span className={`tag-dot tag-color-${t.color.toLowerCase()}`} />
              </span>
            ),
          }))
      }}
      empty="No tag matches."
      footer="Cards with any of the picked tags stay bright."
      matching={matching}
      total={total}
    />
  )
}
