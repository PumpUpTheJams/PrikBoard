import { useEffect, useRef, useState } from 'react'
import type { Assignee, BoardPerson } from '../api/types'

// Limits mirror the API's validation (src/PrikBoard.Api/Contracts/Cards.cs, Domain/CardTags.cs).
export const MAX_TAGS = 10
export const MAX_ASSIGNEES = 10
export const MAX_TAG_LENGTH = 30

/**
 * How the server stores a tag: trimmed, lower-cased, whitespace as hyphens ("Back end" is "back-end").
 * Mirrors CardTags.Normalize in src/PrikBoard.Api/Domain/CardTags.cs; keep the two in sync.
 */
export const normalizeTag = (tag: string) => tag.trim().toLowerCase().replace(/\s+/g, '-')

/** `list` with `item` added, or taken out when it is in there already. */
export const toggleIn = <T>(list: T[], item: T) => (list.includes(item) ? list.filter((x) => x !== item) : [...list, item])

/** How a person shows on a chip or in a list. */
export type KnownPerson = Pick<Assignee, 'name' | 'kind' | 'avatarUrl'>

/** Everyone by principal id: the board's people, then those of `assignees` who aren't (any more) among them. */
export function knownPeople(people: BoardPerson[], assignees: Assignee[]) {
  const known = new Map<string, KnownPerson>(people.map((p) => [p.principalId, p]))
  for (const a of assignees) if (!known.has(a.principalId)) known.set(a.principalId, a)
  return known
}

/**
 * A list that changes on screen at once, while the saves go to the server one after another (each save sends the
 * whole list, so two quick clicks must not race). Follows the server's list again once nothing is pending.
 */
export function useDraftList<T>(server: T[], save: (next: T[]) => Promise<boolean>) {
  const [draft, setDraft] = useState(server)
  const pending = useRef(0)
  const queue = useRef<Promise<unknown>>(Promise.resolve())
  const latest = useRef(server)
  const key = JSON.stringify(server)

  useEffect(() => {
    latest.current = JSON.parse(key) as T[]
    if (pending.current === 0) setDraft(latest.current)
  }, [key])

  function change(next: T[]) {
    setDraft(next)
    pending.current++
    queue.current = queue.current.then(async () => {
      const ok = await save(next)
      pending.current--
      if (!ok && pending.current === 0) setDraft(latest.current)
    })
  }

  return [draft, change] as const
}
