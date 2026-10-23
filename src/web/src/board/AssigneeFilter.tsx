import type { BoardPerson, CardDetail } from '../api/types'
import { Avatar } from '../components/Avatar'
import type { PickerRow } from '../components/PickerWindow'
import { assignedToAny, NOBODY } from './filters'
import { FilterPill } from './FilterPill'
import { UserIcon } from './icons'
import { knownPeople, toggleIn } from './tags'

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
  // Everyone with a name: the board's people, then anyone on a card who has since left the board.
  const known = knownPeople(
    people,
    cards.flatMap((card) => card.assignees).filter((a) => a.name),
  )
  // A public board that hides its people shows visitors nobody to filter on.
  if (known.size === 0 && value.length === 0) return null

  const countOf = (id: string) => cards.filter((n) => assignedToAny(n, [id])).length
  const toggle = (id: string) => onChange(toggleIn(value, id))

  function rows(query: string): PickerRow[] {
    const wanted = query.trim().toLowerCase()
    const named = [...known].filter(([, who]) => who.name.toLowerCase().includes(wanted))
    const nobody = 'no one'.includes(wanted) || 'unassigned'.includes(wanted)
    return [
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
    ]
  }

  return (
    <FilterPill
      icon={<UserIcon size={15} />}
      label="Filter by assignee"
      placeholder="Find someone…"
      chips={value.map((id) => {
        const who = known.get(id)
        const name = id === NOBODY ? 'No one' : (who?.name ?? 'Unknown')
        return { key: id, label: name, leading: who && <Avatar assignee={who} />, className: 'person-chip', onRemove: () => toggle(id) }
      })}
      onClear={() => onChange([])}
      rows={rows}
      empty="No one on this board matches."
      footer="Cards with any of the picked people stay bright."
      matching={matching}
      total={total}
    />
  )
}
