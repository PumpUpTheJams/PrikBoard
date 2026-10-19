import { useEffect, useRef, useState } from 'react'

// Limits mirror the API's validation (src/PrikBoard.Api/Contracts/Notes.cs, Domain/NoteTags.cs).
export const MAX_TAGS = 10
export const MAX_ASSIGNEES = 10
export const MAX_TAG_LENGTH = 30

/**
 * How the server stores a tag: trimmed, lower-cased, whitespace as hyphens ("Back end" is "back-end").
 * Mirrors NoteTags.Normalize in src/PrikBoard.Api/Domain/NoteTags.cs; keep the two in sync.
 */
export const normalizeTag = (tag: string) => tag.trim().toLowerCase().replace(/\s+/g, '-')

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
