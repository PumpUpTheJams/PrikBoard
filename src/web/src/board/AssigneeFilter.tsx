import { useRef, useState } from 'react'
import type { Assignee, BoardPerson, CardDetail } from '../api/types'
import { Avatar } from '../components/Avatar'
import { PickerWindow } from '../components/PickerWindow'
import { assignedToAny, NOBODY } from './filters'
import { CloseIcon, UserIcon } from './icons'

interface AssigneeFilterProps {
  /** Who can be assigned on the board: you first. */
  people: BoardPerson[]
  cards: CardDetail[]
  /** The picked principal ids (and NOBODY); cards with any of them stay bright, the others fade. */
  value: string[]
  onChange: (ids: string[]) => void
  /** Cards that match, of all of them. */
  matching: number
  total: number
}

/** A floating pill next to the tag filter that filters cards by who is assigned. */
export function AssigneeFilter({ people, cards, value, onChange, matching, total }: AssigneeFilterProps) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null)
  const [query, setQuery] = useState('')
  const pill = useRef<HTMLDivElement>(null)

  // Everyone with a name: the board's people, then anyone on a card who has since left the board.
  const known = new Map<string, Pick<Assignee, 'name' | 'kind' | 'avatarUrl'>>(people.map((p) => [p.principalId, p]))
  for (const card of cards) for (const a of card.assignees) if (a.name && !known.has(a.principalId)) known.set(a.principalId, a)
  // A public board that hides its people shows visitors nobody to filter on.
  if (known.size === 0 && value.length === 0) return null

  const countOf = (id: string) => cards.filter((n) => assignedToAny(n, [id])).length
  const toggle = (id: string) => onChange(value.includes(id) ? value.filter((x) => x !== id) : [...value, id])
  const wanted = query.trim().toLowerCase()
  const named = [...known].filter(([, who]) => who.name.toLowerCase().includes(wanted))
  const nobody = 'no one'.includes(wanted) || 'unassigned'.includes(wanted)

  return (
    <div ref={pill} className={`filter-pill${value.length > 0 ? ' active' : ''}`}>
      <button
        type="button"
        className="filter-pill-button"
        aria-haspopup="dialog"
        aria-expanded={anchor !== null}
        onClick={(e) => setAnchor(anchor ? null : e.currentTarget.parentElement!.getBoundingClientRect())}
      >
        <UserIcon size={15} />
        {value.length === 0 ? 'Filter by assignee' : `${matching} of ${total}`}
      </button>
      {value.map((id) => {
        const who = known.get(id)
        const name = id === NOBODY ? 'No one' : (who?.name ?? 'Unknown')
        return (
          <span key={id} className="filter-chip person-chip">
            {who && <Avatar assignee={who} />}
            {name}
            <button type="button" className="chip-remove" aria-label={`Stop filtering on ${name}`} onClick={() => toggle(id)}>
              <CloseIcon size={10} />
            </button>
          </span>
        )
      })}
      {value.length > 0 && (
        <button type="button" className="filter-pill-clear" onClick={() => onChange([])}>
          Clear
        </button>
      )}
      {anchor && (
        <PickerWindow
          anchor={anchor}
          label="Filter by assignee"
          placeholder="Find someone…"
          query={query}
          onQueryChange={setQuery}
          rows={[
            ...named.map(([id, who]) => ({
              key: id,
              label: who.name,
              leading: <Avatar assignee={who} />,
              meta: countOf(id),
              checked: value.includes(id),
              onSelect: () => toggle(id),
            })),
            ...(nobody
              ? [{ key: NOBODY, label: 'No one', detail: 'cards without assignees', meta: countOf(NOBODY), checked: value.includes(NOBODY), onSelect: () => toggle(NOBODY) }]
              : []),
          ]}
          empty="No one on this board matches."
          footer="Cards with any of the picked people stay bright."
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
