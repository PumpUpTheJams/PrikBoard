import { useRef, useState, type ReactNode } from 'react'
import { PickerWindow, type PickerRow } from '../components/PickerWindow'
import { CloseIcon } from './icons'

/** Something picked in a filter, shown as a chip on the pill. */
export interface FilterChip {
  key: string
  label: string
  leading?: ReactNode
  /** Extra class names, after filter-chip. */
  className: string
  onRemove: () => void
}

interface FilterPillProps {
  icon: ReactNode
  /** The button's text while nothing is picked, and the name of its window. */
  label: string
  placeholder: string
  chips: FilterChip[]
  onClear: () => void
  /** The window's rows for what is typed in its search field. */
  rows: (query: string) => PickerRow[]
  empty: string
  footer: string
  /** Cards that match, of all of them. */
  matching: number
  total: number
}

/** A floating filter pill at the bottom left of the board: a button that opens a picker window, and the picks as chips. */
export function FilterPill({ icon, label, placeholder, chips, onClear, rows, empty, footer, matching, total }: FilterPillProps) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null)
  const [query, setQuery] = useState('')
  const pill = useRef<HTMLDivElement>(null)
  const active = chips.length > 0

  return (
    <div ref={pill} className={`filter-pill${active ? ' active' : ''}`}>
      <button
        type="button"
        className="filter-pill-button"
        aria-haspopup="dialog"
        aria-expanded={anchor !== null}
        onClick={(e) => setAnchor(anchor ? null : e.currentTarget.parentElement!.getBoundingClientRect())}
      >
        {icon}
        {active ? `${matching} of ${total}` : label}
      </button>
      {chips.map((chip) => (
        <span key={chip.key} className={`filter-chip ${chip.className}`}>
          {chip.leading}
          {chip.label}
          <button type="button" className="chip-remove" aria-label={`Stop filtering on ${chip.label}`} onClick={chip.onRemove}>
            <CloseIcon size={10} />
          </button>
        </span>
      ))}
      {active && (
        <button type="button" className="filter-pill-clear" onClick={onClear}>
          Clear
        </button>
      )}
      {anchor && (
        <PickerWindow
          anchor={anchor}
          label={label}
          placeholder={placeholder}
          query={query}
          onQueryChange={setQuery}
          rows={rows(query)}
          empty={empty}
          footer={footer}
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
