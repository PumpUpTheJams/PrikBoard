import { useState } from 'react'
import type { Assignee, BoardPerson } from '../api/types'
import { Avatar } from '../components/Avatar'
import { PickerWindow, type PickerRow } from '../components/PickerWindow'
import { knownPeople, MAX_ASSIGNEES, toggleIn, useDraftList } from './tags'

/** How many people the window lists at a time; type to find the others. */
const SHOWN = 5

interface AssigneePickerProps {
  anchor: DOMRect
  /** Who is on the card now (people who left the board stay until they are removed). */
  assignees: Assignee[]
  /** Who can be assigned on the board: you first. */
  people: BoardPerson[]
  /** Saves the card's whole list of principal ids. */
  onChange: (assigneeIds: string[]) => Promise<boolean>
  onClose: () => void
}

/** Picks a card's people and agents from the board's members, searchable by name. */
export function AssigneePicker({ anchor, assignees, people, onChange, onClose }: AssigneePickerProps) {
  const [draft, change] = useDraftList(
    assignees.map((a) => a.principalId),
    onChange,
  )
  const [query, setQuery] = useState('')

  // Everyone with a name: the board's people, plus anyone on the card who has since left the board.
  const known = knownPeople(people, assignees)
  const full = draft.length >= MAX_ASSIGNEES
  const toggle = (id: string) => change(toggleIn(draft, id))

  const wanted = query.trim().toLowerCase()
  const matches = people.filter((p) => p.name.toLowerCase().includes(wanted))
  const shown = matches.slice(0, SHOWN)
  const hidden = matches.length - shown.length

  const detail = (person: BoardPerson) => {
    if (person.isYou) return 'you'
    if (person.kind !== 'Agent') return undefined
    const owner = person.ownerId && people.find((p) => p.principalId === person.ownerId)
    return owner ? `agent · ${owner.isYou ? 'your' : `${owner.name}'s`} token` : 'agent'
  }

  const rows: PickerRow[] = shown.map((person) => {
    const checked = draft.includes(person.principalId)
    return {
      key: person.principalId,
      label: person.name,
      detail: detail(person),
      leading: <Avatar assignee={person} />,
      checked,
      disabled: !checked && full,
      onSelect: () => toggle(person.principalId),
    }
  })

  return (
    <PickerWindow
      anchor={anchor}
      label="Assignees"
      placeholder="Find someone…"
      query={query}
      onQueryChange={setQuery}
      chips={draft.flatMap((id) => {
        const who = known.get(id)
        return who ? [{ key: id, label: who.name, leading: <Avatar assignee={who} />, onRemove: () => toggle(id) }] : []
      })}
      rows={rows}
      empty={wanted ? 'No one on this board matches.' : 'No one to assign.'}
      footer={
        full
          ? `A card has at most ${MAX_ASSIGNEES} people.`
          : hidden > 0
            ? `${hidden} more: type a name to find them.`
            : undefined
      }
      onClose={onClose}
    />
  )
}
