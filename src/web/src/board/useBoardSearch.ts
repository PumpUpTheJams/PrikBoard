import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from 'react'
import type { BoardDetail, CardDetail, Point } from '../api/types'
import type { SearchResult } from './BoardSearch'
import { keyTargetIgnored, shortcut } from './keys'
import { CARD_HEIGHT, CARD_WIDTH } from './layout'
import { reducedMotion } from './motion'
import { matchCard, searchTerms, type SearchField } from './search'

/** A scroll to a card asked for, done after the next render (see useReveal). */
interface Reveal {
  /** Scroll after the next render, once the list of results has its new size. */
  pending: boolean
  /** The card to scroll to instead of the search result: one put back from the trash. */
  id: string | null
  /** Where a smooth scroll is heading: quick key repeats are measured from there, not from a frame halfway. */
  heading: { left: number; top: number; until: number } | null
}

interface BoardSearchOptions {
  board: BoardDetail
  /** The cards on the board (not folded away in a collapsed bucket). */
  visibleCards: CardDetail[]
  /** The collapsed buckets. */
  collapsedIds: Set<string>
  /** Whether the filters fade a card. */
  filterFaded: (card: CardDetail) => boolean
  /** Opens a card, as a card opens anywhere. */
  expand: (id: string) => void
}

/**
 * The board search (BoardSearch): closed, or its words, the result picked and whether its list of results shows. Not
 * kept either. "/" or Ctrl/Cmd+F opens it.
 */
export function useBoardSearch({ board, visibleCards, collapsedIds, filterFaded, expand }: BoardSearchOptions) {
  /** Null when closed; `current` null: the first result. */
  const [search, setSearch] = useState<{ query: string; current: string | null; listOpen: boolean } | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  /** Focus for the search field after the next render: with its words selected, or the text cursor at the end. */
  const focus = useRef<'select' | 'end' | null>(null)
  const reveal = useRef<Reveal>({ pending: false, id: null, heading: null })

  // Fields, menus and other windows keep "/" and Ctrl/Cmd+F; the open card gives way to the search.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      const find = shortcut(e, 'f', { shift: false })
      const slash = e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey
      if (!find && !slash) return
      if (e.target && e.target === inputRef.current) {
        // In the search field, Ctrl/Cmd+F selects its words.
        if (find) {
          e.preventDefault()
          inputRef.current.select()
        }
        return
      }
      if (keyTargetIgnored(e.target, { inCard: true })) return
      e.preventDefault()
      open()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  useLayoutEffect(() => {
    const input = inputRef.current
    if (!focus.current || !input) return
    input.focus({ preventScroll: true })
    if (focus.current === 'select') input.select()
    else input.setSelectionRange(input.value.length, input.value.length)
    focus.current = null
  })

  const terms = useMemo(() => searchTerms(search?.query ?? ''), [search?.query])
  const matches = useMemo(() => {
    const found = new Map<string, SearchField[]>()
    if (terms.length === 0) return found
    for (const card of visibleCards) {
      const fields = matchCard(card, terms)
      if (fields) found.set(card.id, fields)
    }
    return found
  }, [visibleCards, terms])
  // The results are the matches the filters let through, in board order (bucket by bucket, loose cards last).
  const results: SearchResult[] = board.cards.filter((n) => matches.has(n.id) && !filterFaded(n)).map((card) => ({ card, fields: matches.get(card.id)! }))
  /** Cards folded away in collapsed buckets that would be results. */
  const folded = terms.length === 0 ? [] : board.cards.filter((n) => n.bucketId && collapsedIds.has(n.bucketId) && !filterFaded(n) && matchCard(n, terms))
  /** How many matching cards each bucket holds, so the search marks it: the results in it, or the folded ones. */
  const hits = new Map<string, number>()
  for (const { bucketId } of [...results.map((r) => r.card), ...folded]) {
    if (bucketId) hits.set(bucketId, (hits.get(bucketId) ?? 0) + 1)
  }
  /** The result being looked at: the one picked, or else the first. */
  const current = results.find((r) => r.card.id === search?.current)?.card.id ?? results[0]?.card.id ?? null

  /** Opens the search with its words selected. An open card stays open; picking a result swaps it for that one. */
  function open() {
    setSearch((s) => ({ query: s?.query ?? '', current: s?.current ?? null, listOpen: true }))
    focus.current = 'select'
  }

  function close() {
    setSearch(null)
    reveal.current.heading = null
  }

  /** Scrolls to a card after the next render (default: the result being looked at). */
  function revealSoon(id?: string) {
    if (id) reveal.current.id = id
    reveal.current.pending = true
  }

  return {
    state: search,
    inputRef,
    terms,
    matches,
    results,
    folded,
    hits,
    current,
    open,
    close,
    revealSoon,
    /** The card a scroll was asked for (useReveal), once; null when none was. */
    takeReveal(): string | null {
      const r = reveal.current
      if (!r.pending) return null
      r.pending = false
      const id = r.id ?? current
      r.id = null
      return id
    },
    /** Where a smooth scroll to a card is heading, while it is under way. */
    heading: () => {
      const heading = reveal.current.heading
      return heading && heading.until > performance.now() ? heading : null
    },
    setHeading: (to: { left: number; top: number }) => {
      reveal.current.heading = { ...to, until: performance.now() + 600 }
    },
    /** Whether the search fades a card: it has words, and the card has none of them. */
    misses: (card: CardDetail) => terms.length > 0 && !matches.has(card.id),
    setListOpen: (listOpen: boolean) => setSearch((s) => s && { ...s, listOpen }),
    changeQuery(query: string) {
      setSearch((s) => s && { ...s, query, listOpen: true })
      revealSoon()
    },
    /** Moves through the results, wrapping around; the board scrolls to the new one. */
    move(direction: 1 | -1) {
      if (!search) return
      const i = results.findIndex((r) => r.card.id === current)
      const next = results.length > 0 ? results[(i + direction + results.length) % results.length].card.id : null
      setSearch({ ...search, current: next, listOpen: true })
      revealSoon()
    },
    /** Opens a result as a card opens anywhere. The search keeps its words, so Escape comes back to the same results. */
    openResult(id: string) {
      // The opened card takes focus from the search field, so Escape and typing go to it.
      setSearch((s) => s && { ...s, current: id, listOpen: false })
      expand(id)
    },
    /** A press on a card: when it is a result, it becomes the one being looked at. */
    lookAt(id: string) {
      if (search && results.some((r) => r.card.id === id)) setSearch({ ...search, current: id, listOpen: false })
    },
    /** Back to the list of results (an open card was closed), with the text cursor at the end of the words. */
    showResults() {
      if (!search) return
      setSearch({ ...search, listOpen: true })
      focus.current = 'end'
    },
  }
}

export type BoardSearchState = ReturnType<typeof useBoardSearch>

/** A rectangle in the board's viewport, in screen pixels. */
interface Box {
  left: number
  top: number
  right: number
  bottom: number
}

interface RevealOptions {
  scrollRef: RefObject<HTMLDivElement | null>
  /** The search, hints and view-only badge at the top of the board. */
  boardTop: RefObject<HTMLDivElement | null>
  board: BoardDetail
  zoom: number
  /** Where canvas point 0,0 is in the scrolled area. */
  origin: Point
  cardPoint: (card: CardDetail) => Point
}

/**
 * Does the scroll a search asked for after a render. Call it after the board's view (useBoardView), so the scroll
 * position has followed a change of the canvas's top-left first.
 */
export function useReveal(search: BoardSearchState, { scrollRef, boardTop, board, zoom, origin, cardPoint }: RevealOptions) {
  /**
   * Scrolls the board so a card is on screen and not under the search, its results or another floating control. When
   * it has to move, it lands in the middle of the space below the search.
   */
  function scrollTo(id: string) {
    const el = scrollRef.current
    const card = board.cards.find((n) => n.id === id)
    if (!el || !card) return
    const frame = el.getBoundingClientRect()
    const from = search.heading() ?? { left: el.scrollLeft, top: el.scrollTop }
    const at = cardPoint(card)
    const left = at.x * zoom + origin.x - from.left
    const top = at.y * zoom + origin.y - from.top
    const box: Box = { left, top, right: left + CARD_WIDTH * zoom, bottom: top + CARD_HEIGHT * zoom }
    // Floating controls, with a little room around them.
    const around = (node: Element | null | undefined): Box | null => {
      const r = node?.getBoundingClientRect()
      if (!r || r.width === 0) return null
      return { left: r.left - frame.left - 8, top: r.top - frame.top - 8, right: r.right - frame.left + 8, bottom: r.bottom - frame.top + 8 }
    }
    const section = el.parentElement
    const stack = around(boardTop.current)
    const rail = around(section?.querySelector('.tool-rail'))
    const covers = [stack, rail, around(section?.querySelector('.view-controls')), around(section?.querySelector('.board-filters'))]
    const covered = covers.some((r) => r && box.left < r.right && box.right > r.left && box.top < r.bottom && box.bottom > r.top)
    if (!covered && box.left >= 0 && box.top >= 0 && box.right <= el.clientWidth && box.bottom <= el.clientHeight) return
    const area: Box = { left: rail?.right ?? 16, top: stack?.bottom ?? 16, right: el.clientWidth - 16, bottom: el.clientHeight - 16 }
    const x = (area.left + area.right) / 2
    const y = Math.max(area.top + (CARD_HEIGHT * zoom) / 2, (area.top + area.bottom) / 2)
    const to = {
      left: Math.min(Math.max(0, (at.x + CARD_WIDTH / 2) * zoom + origin.x - x), el.scrollWidth - el.clientWidth),
      top: Math.min(Math.max(0, (at.y + CARD_HEIGHT / 2) * zoom + origin.y - y), el.scrollHeight - el.clientHeight),
    }
    search.setHeading(to)
    el.scrollTo({ ...to, behavior: reducedMotion() ? 'auto' : 'smooth' })
  }

  useLayoutEffect(() => {
    const id = search.takeReveal()
    if (id) scrollTo(id)
  })
}
