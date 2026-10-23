import type { CardColor, CardComponent, CardDetail } from '../api/types'
import { componentKinds } from './componentKinds'

/*
 * Searching a board's cards (BoardSearch). Case and accents don't matter, and with several words a card matches
 * when every word is found somewhere on it: its title, readable id (MPCT-415), tags, people, who created it, or its
 * components. "Quoted words" are looked for together, and a prefix keeps a word to one field: `tag:ui`,
 * `assignee:jimmy`, `creator:jimmy` or `color:red` (the fill's name or a plain color word; only with the prefix).
 */

/** Lower case without accents, so "cafe" finds "café". */
export const fold = (text: string) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()

/** One searchable piece of a card. */
export interface SearchField {
  /** The title and tags are on the card on the board, where their matches are marked already. */
  kind: 'title' | 'id' | 'tag' | 'assignee' | 'creator' | 'color' | 'component'
  /** What it is, as shown under a result: "Tag", "Created by", or the component's title or kind. */
  label: string
  text: string
}

/** The fields a word can be kept to with a prefix. */
export type SearchPrefix = 'tag' | 'assignee' | 'creator' | 'color'

/** A word (or quoted words) to look for, folded; `field` keeps it to that kind of field. */
export interface SearchTerm {
  text: string
  field?: SearchPrefix
}

const prefixes: Record<string, SearchPrefix> = {
  tag: 'tag',
  tags: 'tag',
  assignee: 'assignee',
  assigned: 'assignee',
  creator: 'creator',
  created: 'creator',
  color: 'color',
  colour: 'color',
}

/** Plain color words for each fill, so `color:red` finds Coral cards without knowing the fills' names. */
const colorWords: Record<CardColor, string[]> = {
  Butter: ['butter', 'yellow'],
  Mint: ['mint', 'green'],
  Sky: ['sky', 'blue'],
  Coral: ['coral', 'red', 'pink', 'orange'],
  Lilac: ['lilac', 'purple', 'violet'],
}

/**
 * The words to look for; none for an empty query. `prefix:word` (a space after the colon is fine too) keeps the word
 * to that field; an unknown prefix is part of the word, so "https://…" is still found. A prefix without a word yet
 * (while typing) looks for nothing.
 */
export function searchTerms(query: string): SearchTerm[] {
  const terms: SearchTerm[] = []
  const plain = (text: string) => {
    const words = text.replace(/\s+/g, ' ').trim()
    if (words) terms.push({ text: words })
  }
  for (const [all, name, value, quoted] of fold(query).matchAll(/(\p{L}+):\s*("[^"]*"?|[^\s"]*)|"([^"]*)"?|[^\s"]+/gu)) {
    if (name === undefined) {
      plain(quoted ?? all)
      continue
    }
    const field = prefixes[name]
    if (!field) {
      for (const word of all.split(/\s+/)) plain(word)
      continue
    }
    const text = value.replace(/"/g, '').replace(/\s+/g, ' ').trim()
    if (text) terms.push({ text, field })
  }
  return terms
}

/** The values a prefix can take on this board, for suggestions: tag names, people's names, creators' names. */
export type SearchValues = Record<Exclude<SearchPrefix, 'color'>, string[]>

/** A way to finish the word being typed: `insert` replaces the query's text from `start` to `end`. */
export interface SearchSuggestion {
  start: number
  end: number
  insert: string
  /** What the list shows. */
  label: string
  /** What it is: the prefix's field, or a fill's plain color word. */
  hint?: string
  /** What it adds to the word typed so far, when it starts with that word: shown greyed out after it in the field. */
  rest?: string
}

/** The prefixes suggested, in this order, by their main names. */
const prefixHints: Record<SearchPrefix, string> = {
  tag: 'Tag',
  assignee: 'Assigned to',
  creator: 'Created by',
  color: 'Fill color',
}

/** At most this many values are suggested; typing narrows them. */
const MAX_SUGGESTIONS = 8

/**
 * Suggestions for the word at `caret` (BoardSearch shows them; Tab takes the first): with an empty query, or while a
 * word could still become a prefix, the prefixes (`ta` → `tag:`); after a prefix, the values it can take on this
 * board, those starting with what was typed first. A value with a space is quoted, and taking one adds a space after
 * it for the next word.
 */
export function searchSuggestions(query: string, caret: number, values: SearchValues): SearchSuggestion[] {
  const before = query.slice(0, caret)
  // The rest of the word the caret is in is replaced too.
  const end = caret + (/^[^\s"]*/.exec(query.slice(caret))?.[0].length ?? 0)

  const prefixed = /(\p{L}+):("[^"]*|[^\s"]*)$/u.exec(before)
  const field = prefixed && prefixes[fold(prefixed[1])]
  if (prefixed && field) {
    const raw = prefixed[2].replace(/^"/, '')
    const typed = fold(raw)
    const start = before.length - prefixed[0].length
    const fills = Object.keys(colorWords) as CardColor[]
    const options =
      field !== 'color'
        ? [...new Set(values[field])].map((v) => ({ value: v, words: [fold(v)], hint: undefined as string | undefined }))
        : !typed
          ? // The fills by name, with their plain color words.
            fills.map((c) => ({ value: c.toLowerCase(), words: colorWords[c], hint: colorWords[c].slice(1).join(', ') }))
          : // Any color word (`red`), naming its fill, so the one being typed can be finished.
            fills.flatMap((c) =>
              colorWords[c].map((w, i) => ({ value: w, words: [w], hint: i === 0 ? colorWords[c].slice(1).join(', ') : c })),
            )
    const starts = (o: (typeof options)[number]) => o.words.some((w) => w.startsWith(typed))
    // A color is found by the start of its words only (`found`); other values anywhere in them.
    const within = field === 'color' ? [] : options.filter((o) => !starts(o) && o.words.some((w) => w.includes(typed)))
    return [...options.filter(starts), ...within]
      .slice(0, MAX_SUGGESTIONS)
      .map((o) => ({
        start,
        end,
        insert: `${prefixed[1]}:${/\s/.test(o.value) ? `"${o.value}"` : o.value} `,
        label: o.value,
        hint: o.hint || undefined,
        rest: fold(o.value).startsWith(typed) ? o.value.slice(raw.length) : undefined,
      }))
  }

  const word = /[^\s"]*$/.exec(before)![0]
  if (word.includes(':') || (!word && query.trim())) return []
  const typed = fold(word)
  return (Object.keys(prefixHints) as SearchPrefix[])
    .filter((p) => p.startsWith(typed))
    .map((p) => ({ start: caret - word.length, end, insert: `${p}:`, label: `${p}:`, hint: prefixHints[p], rest: word ? `${p}:`.slice(word.length) : undefined }))
}

/** Whether a term searches a kind of field: a prefixed term only its own kind, others every kind but the color. */
const looksIn = (term: SearchTerm, kind: SearchField['kind']) => (term.field ? term.field === kind : kind !== 'color')

/** The words of the terms that search a kind of field. */
export const wordsFor = (terms: SearchTerm[], kind: SearchField['kind']) => terms.filter((t) => looksIn(t, kind)).map((t) => t.text)

/** Whether a term is found in a field: a color by the start of one of its words, the rest anywhere in the text. */
function found(term: SearchTerm, kind: SearchField['kind'], text: string) {
  if (!looksIn(term, kind)) return false
  if (kind === 'color') return (colorWords[text as CardColor] ?? [fold(text)]).some((w) => w.startsWith(term.text))
  return fold(text).includes(term.text)
}

/** Where the terms that search a field occur in its text, to mark them; a matching color is marked whole. */
export function fieldRanges(text: string, terms: SearchTerm[], kind: SearchField['kind']): [number, number][] {
  if (kind === 'color') return terms.some((t) => found(t, kind, text)) ? [[0, text.length]] : []
  return matchRanges(text, wordsFor(terms, kind))
}

/** The text in a component: its title, text, checklist items, and its links' titles and web addresses. */
function componentTexts(component: CardComponent): string[] {
  const texts = [component.title, component.text, ...(component.items ?? []).map((i) => i.text)]
  for (const link of component.links ?? []) texts.push(link.title, /^https?:/i.test(link.url) ? link.url : null)
  return texts.filter((t): t is string => !!t)
}

export const componentMatches = (component: CardComponent, terms: SearchTerm[]) =>
  componentTexts(component).some((text) => terms.some((t) => found(t, 'component', text)))

function cardFields(card: CardDetail): SearchField[] {
  const fields: SearchField[] = [
    { kind: 'title', label: 'Title', text: card.title },
    { kind: 'id', label: 'ID', text: card.readableId },
    { kind: 'color', label: 'Color', text: card.color },
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
export function matchCard(card: CardDetail, terms: SearchTerm[]): SearchField[] | null {
  const fields = cardFields(card)
  if (!terms.every((t) => fields.some((f) => found(t, f.kind, f.text)))) return null
  return fields.filter((f) => terms.some((t) => found(t, f.kind, f.text)))
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
