import { useRef, useState } from 'react'
import type { BoardTag } from '../api/types'
import { PickerWindow } from '../components/PickerWindow'
import { CloseIcon, FilterIcon } from './icons'
import { tagColorClass, type TagColors } from './tagColors'
import { normalizeTag } from './tags'

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
  const [anchor, setAnchor] = useState<DOMRect | null>(null)
  const [query, setQuery] = useState('')
  const pill = useRef<HTMLDivElement>(null)
  if (boardTags.length === 0 && value.length === 0) return null

  const wanted = normalizeTag(query)
  const toggle = (name: string) => onChange(value.includes(name) ? value.filter((t) => t !== name) : [...value, name])

  return (
    <div ref={pill} className={`filter-pill${value.length > 0 ? ' active' : ''}`}>
      <button
        type="button"
        className="filter-pill-button"
        aria-haspopup="dialog"
        aria-expanded={anchor !== null}
        onClick={(e) => setAnchor(anchor ? null : e.currentTarget.parentElement!.getBoundingClientRect())}
      >
        <FilterIcon />
        {value.length === 0 ? 'Filter by tag' : `${matching} of ${total}`}
      </button>
      {value.map((tag) => (
        <span key={tag} className={`filter-chip tag-filter-chip${tagColorClass(tagColors, tag)}`}>
          {tag}
          <button type="button" className="chip-remove" aria-label={`Stop filtering on ${tag}`} onClick={() => toggle(tag)}>
            <CloseIcon size={10} />
          </button>
        </span>
      ))}
      {value.length > 0 && (
        <button type="button" className="filter-pill-clear" onClick={() => onChange([])}>
          Clear
        </button>
      )}
      {anchor && (
        <PickerWindow
          anchor={anchor}
          label="Filter by tag"
          placeholder="Find a tag…"
          query={query}
          onQueryChange={setQuery}
          rows={boardTags
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
            }))}
          empty="No tag matches."
          footer="Cards with any of the picked tags stay bright."
          owner={pill}
          onClose={() => {
            setAnchor(null)
            setQuery('')
          }}
        />
      )}
    </div>
  )
}
