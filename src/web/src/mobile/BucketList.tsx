import type { ReactNode } from 'react'
import type { BoardDetail, CardDetail } from '../api/types'
import { Card } from '../board/Card'
import { BucketIcon, ChevronRightIcon, CloseIcon, SearchIcon, SparkleIcon } from '../board/icons'
import { bucketPath, cardLabel, LOOSE } from '../board/links'
import { foundByNumber, matchCard, searchTerms, type SearchTerm } from '../board/search'
import type { TagColors } from '../board/tagColors'
import { bucketItems, bucketsInOrder, hasLoose, itemCount } from './items'
import { navigate } from './nav'
import { BarButton, TopBar } from './parts'

interface BucketListProps {
  board: BoardDetail
  tagColors: TagColors
  /** The search over the whole board, or null while it is closed. */
  query: string | null
  onQuery: (query: string | null) => void
  actions: ReactNode
}

/** The second mobile screen: the board's buckets, to pick one, or the cards of the whole board a search finds. */
export function BucketList({ board, tagColors, query, onQuery, actions }: BucketListProps) {
  const buckets = bucketsInOrder(board)
  const terms = searchTerms(query ?? '')

  return (
    <div className="m-screen">
      <TopBar up={{ to: '', label: 'Back to boards' }} title={board.name} context={<span className="board-key">{board.key}</span>}>
        <BarButton label={query === null ? 'Search the board' : 'Close search'} onClick={() => onQuery(query === null ? '' : null)}>
          {query === null ? <SearchIcon size={20} /> : <CloseIcon size={20} />}
        </BarButton>
        {actions}
      </TopBar>

      {query !== null && (
        <label className="m-search in-bar">
          <SearchIcon />
          <input type="search" autoFocus aria-label={`Search cards on ${board.name}`} placeholder="Search the whole board" value={query} onChange={(e) => onQuery(e.target.value)} />
        </label>
      )}

      {query !== null && terms.length > 0 ? (
        <SearchResults board={board} terms={terms} tagColors={tagColors} />
      ) : (
        <div className="m-scroll">
          <div className="m-heading">
            <h1>
              Buckets <span className="m-count">{board.cards.length === 1 ? '1 card' : `${board.cards.length} cards`}</span>
            </h1>
          </div>

          <ul className="m-list">
            {buckets.map((bucket) => {
              const items = bucketItems(board, bucket.id)
              const working = items.filter((i) => i.kind === 'card' && i.card.agentWorking).length
              const first = items.find((i) => i.kind === 'card')
              return (
                <li key={bucket.id}>
                  <button type="button" className={`m-bucket${bucket.collapsed ? ' collapsed' : ''}`} onClick={() => navigate(bucketPath(board.shortId, bucket.id))}>
                    <span className="m-row-text">
                      <span className="m-row-title">
                        <span className="m-row-name">{bucket.name}</span>
                        {working > 0 && (
                          <span className="m-agents" title="Cards an agent is working on">
                            <SparkleIcon />
                            {working === 1 ? '1 agent' : `${working} agents`}
                          </span>
                        )}
                      </span>
                      <span className="m-row-detail">
                        {bucket.collapsed ? `Collapsed · ${itemCount(items)}` : first?.kind === 'card' ? `${itemCount(items)} · first: ${cardLabel(first.card)}` : itemCount(items)}
                      </span>
                    </span>
                    <span className="count">{items.length}</span>
                    <ChevronRightIcon />
                  </button>
                </li>
              )
            })}
            {hasLoose(board) && (
              <li>
                <button type="button" className="m-bucket loose" onClick={() => navigate(bucketPath(board.shortId, LOOSE))}>
                  <BucketIcon />
                  <span className="m-row-text">
                    <span className="m-row-name">Not in a bucket</span>
                  </span>
                  <span className="count">{bucketItems(board, LOOSE).length}</span>
                  <ChevronRightIcon />
                </button>
              </li>
            )}
          </ul>
          {buckets.length === 0 && !hasLoose(board) && <p className="m-empty">This board is empty. Add buckets and cards on a larger screen.</p>}
        </div>
      )}
    </div>
  )
}

/** The cards on the board that match, under the bucket each is in (in the buckets' order), a card found by its number first. */
function SearchResults({ board, terms, tagColors }: { board: BoardDetail; terms: SearchTerm[]; tagColors: TagColors }) {
  const groups = [...bucketsInOrder(board).map((b) => ({ id: b.id, name: b.name })), { id: LOOSE, name: 'Not in a bucket' }]
    .map((group) => {
      const cards = bucketItems(board, group.id).flatMap((i) => (i.kind === 'card' && matchCard(i.card, terms) ? [i.card] : []))
      return { ...group, cards: [...cards.filter((c) => foundByNumber(c, terms)), ...cards.filter((c) => !foundByNumber(c, terms))] }
    })
    .filter((g) => g.cards.length > 0)
  const found = groups.reduce((n, g) => n + g.cards.length, 0)
  const attached = (card: CardDetail) => board.images.filter((i) => i.cardId === card.id).length

  return (
    <div className="m-scroll" aria-live="polite">
      <p className="m-found">{found === 0 ? 'No card on this board matches.' : found === 1 ? '1 card found' : `${found} cards found`}</p>
      {groups.map((group) => (
        <section key={group.id} className="m-found-group" aria-label={group.name}>
          <h2>
            {group.name} <span className="m-count">{group.cards.length}</span>
          </h2>
          {group.cards.map((card) => (
            <Card
              key={card.id}
              card={card}
              images={attached(card)}
              tagColors={tagColors}
              selected={false}
              lifted={false}
              highlight={terms}
              onExpand={() => navigate(card.path)}
            />
          ))}
        </section>
      ))}
    </div>
  )
}
