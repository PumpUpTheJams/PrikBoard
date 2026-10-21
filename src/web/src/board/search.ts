import type { CardComponent, CardDetail } from '../api/types'
import { componentKinds } from './componentKinds'

/*
 * Searching a board's cards (BoardSearch). Case and accents don't matter, and with several words a card matches
 * when every word is found somewhere on it: its title, readable id (MPCT-415), tags, people, who created it, or its components.
 */

/** Lower case without accents, so "cafe" finds "café". */
export const fold = (text: string) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()

/** The words to look for; none for an empty query. */
export const searchTerms = (query: string) => fold(query).split(/\s+/).filter(Boolean)

export const textMatches = (text: string, terms: string[]) => {
  const folded = fold(text)
  return terms.some((t) => folded.includes(t))
}

/** One searchable piece of a card. */
export interface SearchField {
  /** The title and tags are on the card on the board, where their matches are marked already. */
  kind: 'title' | 'id' | 'tag' | 'assignee' | 'creator' | 'component'
  /** What it is, as shown under a result: "Tag", "Created by", or the component's title or kind. */
  label: string
  text: string
}

/** The text in a component: its title, text, checklist items, and its links' titles and web addresses. */
function componentTexts(component: CardComponent): string[] {
  const texts = [component.title, component.text, ...(component.items ?? []).map((i) => i.text)]
  for (const link of component.links ?? []) texts.push(link.title, /^https?:/i.test(link.url) ? link.url : null)
  return texts.filter((t): t is string => !!t)
}

export const componentMatches = (component: CardComponent, terms: string[]) => componentTexts(component).some((t) => textMatches(t, terms))

function cardFields(card: CardDetail): SearchField[] {
  const fields: SearchField[] = [
    { kind: 'title', label: 'Title', text: card.title },
    { kind: 'id', label: 'ID', text: card.readableId },
  ]
  for (const tag of card.tags) fields.push({ kind: 'tag', label: 'Tag', text: tag })
  for (const a of card.assignees) fields.push({ kind: 'assignee', label: 'Assigned to', text: a.name })
  if (card.createdBy) fields.push({ kind: 'creator', label: 'Created by', text: card.createdBy })
  for (const component of card.components) {
    const label = component.title ?? componentKinds[component.kind].label
    for (const text of componentTexts(component)) fields.push({ kind: 'component', label, text })
  }
  return fields
}

/** The fields where any of the terms occur, or null when some term occurs nowhere on the card. */
export function matchCard(card: CardDetail, terms: string[]): SearchField[] | null {
  const fields = cardFields(card).map((field) => ({ field, folded: fold(field.text) }))
  if (!terms.every((t) => fields.some((f) => f.folded.includes(t)))) return null
  return fields.filter((f) => terms.some((t) => f.folded.includes(t))).map((f) => f.field)
}

/** Where the terms occur in `text`: [start, end) offsets into it, sorted, with overlapping ones merged. */
export function matchRanges(text: string, terms: string[]): [number, number][] {
  if (terms.length === 0) return []
  // Folding can change the length of a character ("é" loses its accent), so remember where each folded unit came from.
  let folded = ''
  const starts: number[] = []
  const ends: number[] = []
  let at = 0
  for (const char of text) {
    const f = fold(char)
    folded += f
    for (let i = 0; i < f.length; i++) {
      starts.push(at)
      ends.push(at + char.length)
    }
    at += char.length
  }
  const found: [number, number][] = []
  for (const term of terms) {
    for (let i = folded.indexOf(term); i !== -1; i = folded.indexOf(term, i + term.length)) found.push([starts[i], ends[i + term.length - 1]])
  }
  found.sort((a, b) => a[0] - b[0])
  const merged: [number, number][] = []
  for (const range of found) {
    const last = merged[merged.length - 1]
    if (last && range[0] <= last[1]) last[1] = Math.max(last[1], range[1])
    else merged.push(range)
  }
  return merged
}

/** A long text on one line, cut down to the part around its first match, with an ellipsis where it was cut. */
export function excerpt(text: string, terms: string[], room = 24): string {
  const line = text.replace(/\s+/g, ' ').trim()
  const first = matchRanges(line, terms)[0]
  if (!first || line.length <= room * 2 + 12) return line
  // Cut between words where there is one close by.
  let start = Math.max(0, first[0] - room)
  let end = Math.min(line.length, first[1] + room)
  const before = line.indexOf(' ', start)
  if (start > 0 && before !== -1 && before < first[0]) start = before + 1
  const after = line.lastIndexOf(' ', end)
  if (end < line.length && after > first[1]) end = after
  return `${start > 0 ? '…' : ''}${line.slice(start, end).trim()}${end < line.length ? '…' : ''}`
}
