import { Fragment, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import type { CardDetail } from '../api/types'
import { Highlight } from './Highlight'
import { cardLabel } from './links'
import { ChevronDownIcon, ChevronUpIcon, CloseIcon, SearchIcon } from './icons'
import { excerpt, searchSuggestions, wordsFor, type SearchField, type SearchSuggestion, type SearchTerm, type SearchValues } from './search'

export interface SearchResult {
  card: CardDetail
  /** Where the search words were found on it. */
  fields: SearchField[]
}

interface BoardSearchProps {
  inputRef: RefObject<HTMLInputElement | null>
  query: string
  onQueryChange: (query: string) => void
  terms: SearchTerm[]
  /** Matching cards that pass the filters, in board order. */
  results: SearchResult[]
  /** The active result: outlined on the board, highlighted in the list. */
  current: string | null
  /** The list folds away while you work on the board or in the open card, and comes back when you return to the field. */
  listOpen: boolean
  onListOpenChange: (open: boolean) => void
  onMove: (direction: 1 | -1) => void
  onOpen: (cardId: string) => void
  onClose: () => void
  /** Matches the filters fade, so they aren't in the results. */
  faded: number
  /** Matches folded away in collapsed buckets, which the search marks instead. */
  folded: number
  onClearFilter: () => void
  bucketName: (card: CardDetail) => string
  /** What the prefixes can take on this board, for suggestions. */
  values: SearchValues
}

/**
 * The search at the top of the board, opened with / or Ctrl/Cmd+F: a field with the number of matches, and the matching
 * cards listed under it. ↑ and ↓ move through them, Enter opens one, Escape closes the search. Under the field,
 * suggestions for the word being typed (prefixes, then the values a prefix can take); Tab takes the first.
 */
export function BoardSearch(props: BoardSearchProps) {
  const { inputRef, query, onQueryChange, terms, results, current, listOpen, onListOpenChange, onMove, onOpen, onClose } = props
  const { faded, folded, onClearFilter, bucketName, values } = props
  const listRef = useRef<HTMLUListElement>(null)
  /** Where the text cursor is in the field; null while it isn't in the field. */
  const [caret, setCaret] = useState<number | null>(null)
  const searching = terms.length > 0
  const showList = listOpen && searching
  const index = results.findIndex((r) => r.card.id === current)
  const suggestions = listOpen && caret !== null ? searchSuggestions(query, Math.min(caret, query.length), values) : []
  /** The rest of the first suggestion, greyed out after the text when the cursor is at its end. */
  const ghost = caret === query.length ? suggestions[0]?.rest : undefined

  /** Puts a suggestion in the query, with the text cursor after it. */
  function take({ start, end, insert }: SearchSuggestion) {
    const next = query.slice(0, start) + insert + query.slice(end).replace(/^ (?=\S|$)/, '')
    const at = start + insert.length
    onQueryChange(next)
    setCaret(at)
    requestAnimationFrame(() => inputRef.current?.setSelectionRange(at, at))
  }
  const trackCaret = (input: HTMLInputElement) => setCaret(input.selectionStart === input.selectionEnd ? input.selectionStart : null)

  // Keeps the active result in view in the list.
  useLayoutEffect(() => {
    const list = listRef.current
    const row = current ? list?.querySelector<HTMLElement>(`[data-result="${current}"]`) : null
    if (!list || !row) return
    if (row.offsetTop < list.scrollTop) list.scrollTop = row.offsetTop - 4
    else if (row.offsetTop + row.offsetHeight > list.scrollTop + list.clientHeight)
      list.scrollTop = row.offsetTop + row.offsetHeight - list.clientHeight + 4
  }, [current, showList])

  return (
    <>
      <div
        className={`board-search${searching ? ' active' : ''}`}
        role="search"
        // Pressing the arrows keeps the text cursor in the field.
        onMouseDown={(e) => e.target instanceof Element && e.target.closest('button') && e.preventDefault()}
      >
        <SearchIcon />
        <span className="search-field">
          {ghost && (
            // The typed text, invisible, keeps the suggestion's rest right after it.
            <span className="search-ghost" aria-hidden="true">
              <span className="search-ghost-typed">{query}</span>
              {ghost}
            </span>
          )}
          <input
          ref={inputRef}
          type="search"
          role="combobox"
          aria-label="Search this board"
          aria-autocomplete="list"
          aria-expanded={showList}
          aria-controls="board-search-results"
          aria-activedescendant={showList && current ? `search-result-${current}` : undefined}
          placeholder="Search titles, tags, content, people…"
          title="Keep a word to one field with a prefix: tag:ui, assignee:name, creator:name or color:red"
          autoComplete="off"
          spellCheck={false}
          value={query}
          onChange={(e) => {
            onQueryChange(e.target.value)
            trackCaret(e.target)
          }}
          onSelect={(e) => trackCaret(e.currentTarget)}
          onFocus={(e) => {
            onListOpenChange(true)
            trackCaret(e.currentTarget)
          }}
          onBlur={(e) => {
            if (!(e.relatedTarget instanceof Node && e.currentTarget.closest('.board-top')?.contains(e.relatedTarget))) onListOpenChange(false)
          }}
          onKeyDown={(e) => {
            // Tab takes the first suggestion, and → at the end of the text the one greyed out there, as in a shell.
            if ((e.key === 'Tab' && !e.shiftKey && suggestions.length > 0) || (e.key === 'ArrowRight' && ghost)) {
              e.preventDefault()
              take(suggestions[0])
            } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
              e.preventDefault()
              onMove(e.key === 'ArrowDown' ? 1 : -1)
            } else if (e.key === 'Enter') {
              e.preventDefault()
              if (current) onOpen(current)
            } else if (e.key === 'Escape') {
              e.preventDefault()
              e.stopPropagation()
              onClose()
            }
          }}
          />
        </span>
        <span className={`search-count${searching && results.length === 0 ? ' none' : ''}`} aria-live="polite">
          {!searching ? '' : results.length === 0 ? 'No matches' : `${index + 1} of ${results.length}`}
        </span>
        <span className="search-divider" aria-hidden="true" />
        <button
          type="button"
          className="search-button"
          aria-label="Previous result"
          title="Previous result (↑)"
          disabled={results.length < 2}
          onClick={() => onMove(-1)}
        >
          <ChevronUpIcon size={16} />
        </button>
        <button
          type="button"
          className="search-button"
          aria-label="Next result"
          title="Next result (↓)"
          disabled={results.length < 2}
          onClick={() => onMove(1)}
        >
          <ChevronDownIcon size={16} />
        </button>
        <button type="button" className="search-button" aria-label="Close search" title="Close search (Esc)" onClick={onClose}>
          <CloseIcon />
        </button>
      </div>

      {suggestions.length > 0 && (
        // A press keeps the text cursor in the field.
        <div className="search-suggestions" role="listbox" aria-label="Suggestions" onMouseDown={(e) => e.preventDefault()}>
          {suggestions.map((s, i) => (
            <button
              key={s.label}
              type="button"
              role="option"
              aria-selected={i === 0}
              className={`search-suggestion${i === 0 ? ' first' : ''}`}
              onClick={() => take(s)}
            >
              <span className="search-suggestion-label">{s.label}</span>
              {s.hint && <span className="search-suggestion-hint">{s.hint}</span>}
            </button>
          ))}
          <span className="search-suggestions-key">
            <kbd>Tab</kbd>
          </span>
        </div>
      )}

      {showList && (
        <div className="search-results" onMouseDown={(e) => e.preventDefault()}>
          <ul ref={listRef} id="board-search-results" className="search-result-list" role="listbox" aria-label="Search results">
            {results.length === 0 ? (
              <li className="search-empty" role="presentation">
                {faded > 0 ? 'None of the cards the filters show' : folded > 0 ? 'No open card' : 'No card on this board'} matches “{query.trim()}”.
              </li>
            ) : (
              results.map(({ card, fields }) => (
                <li
                  key={card.id}
                  id={`search-result-${card.id}`}
                  data-result={card.id}
                  role="option"
                  aria-selected={card.id === current}
                  className={`search-result${card.id === current ? ' active' : ''}`}
                  onClick={() => onOpen(card.id)}
                >
                  <span className={`link-swatch fill-${card.color.toLowerCase()}`} aria-hidden="true" />
                  <span className="search-result-text">
                    <span className="search-result-title">
                      <Highlight text={cardLabel(card)} terms={terms} field="title" />
                    </span>
                    <span className="search-result-where">
                      <FoundIn fields={fields} terms={terms} />
                    </span>
                  </span>
                  <span className="search-result-bucket">{bucketName(card)}</span>
                </li>
              ))
            )}
          </ul>
          <div className="search-results-foot">
            {results.length > 0 && (
              <>
                <span>
                  <kbd>↑</kbd>
                  <kbd>↓</kbd> move
                </span>
                <span>
                  <kbd>Enter</kbd> open
                </span>
              </>
            )}
            <span>
              <kbd>Esc</kbd> close
            </span>
            {folded > 0 && <span className="search-faded">{folded} more in collapsed buckets, marked on the board</span>}
            {faded > 0 && (
              <span className="search-faded">
                {faded} more faded by the filters
                <button type="button" onClick={onClearFilter}>
                  Clear the filters
                </button>
              </span>
            )}
          </div>
        </div>
      )}
    </>
  )
}

/**
 * Where a result matched: first what the card on the board doesn't show (a checklist item, who created it), then its
 * tags. The title is marked in the row already.
 */
function FoundIn({ fields, terms }: { fields: SearchField[]; terms: SearchTerm[] }) {
  const seen = new Set<string>()
  const shown = [...fields.filter((f) => f.kind !== 'title' && f.kind !== 'tag'), ...fields.filter((f) => f.kind === 'tag')]
    .filter((f) => !seen.has(f.label + f.text) && !!seen.add(f.label + f.text))
    .slice(0, 2)
  if (shown.length === 0) return <span className="search-where-label">In the title</span>
  return shown.map((f, i) => (
    <Fragment key={i}>
      {i > 0 && ' · '}
      <span className="search-where-label">{f.label}</span>
      <Highlight text={f.kind === 'component' ? excerpt(f.text, wordsFor(terms, 'component')) : f.text} terms={terms} field={f.kind} />
    </Fragment>
  ))
}
