import { useState, type ReactNode } from 'react'
import type { BoardDetail, CardDetail } from '../api/types'
import { BoardImageView } from '../board/BoardImageView'
import { Card } from '../board/Card'
import { CloseIcon, SearchIcon } from '../board/icons'
import { bucketPath, imagePath, LOOSE } from '../board/links'
import { ops } from '../board/ops'
import { matchCard, searchTerms } from '../board/search'
import type { TagColors } from '../board/tagColors'
import type { RecordingRun } from '../board/undo'
import { bucketItems, bucketsInOrder, hasLoose } from './items'
import { navigate } from './nav'
import { AddButton, BarButton, TopBar } from './parts'

interface BucketScreenProps {
  board: BoardDetail
  /** The bucket shown, or LOOSE for the cards in none. */
  bucketId: string
  tagColors: TagColors
  run: RecordingRun
  /** Opens a card that was just made, with its title ready to type. */
  onCreated: (card: CardDetail) => void
  actions: ReactNode
}

/** The third mobile screen: one bucket's cards and images, scrolled through, with the other buckets as tabs above. */
export function BucketScreen({ board, bucketId, tagColors, run, onCreated, actions }: BucketScreenProps) {
  const [query, setQuery] = useState<string | null>(null)
  const terms = searchTerms(query ?? '')
  const buckets = bucketsInOrder(board)
  const bucket = buckets.find((b) => b.id === bucketId)
  const tabs = [...buckets.map((b) => ({ id: b.id, name: b.name })), ...(hasLoose(board) ? [{ id: LOOSE, name: 'Not in a bucket' }] : [])]
  const items = bucketItems(board, bucketId).filter((i) => terms.length === 0 || (i.kind === 'card' && matchCard(i.card, terms)))
  const attached = (card: CardDetail) => board.images.filter((i) => i.cardId === card.id).length
  const canAdd = board.yourRole !== 'Viewer' && bucket !== undefined

  async function addCard() {
    if (!bucket) return
    const [add, describe] = ops.addCard(board.id, { bucketId: bucket.id })
    const made: { card?: CardDetail } = {}
    await run(async () => (made.card = await add()), describe)
    if (made.card) onCreated(made.card)
  }

  return (
    <div className="m-screen">
      <TopBar up={{ to: `/#/boards/${board.shortId}`, label: 'Back to buckets' }} context={board.name} title={bucket?.name ?? 'Not in a bucket'}>
        <BarButton label={query === null ? 'Search cards' : 'Close search'} onClick={() => setQuery(query === null ? '' : null)}>
          {query === null ? <SearchIcon size={20} /> : <CloseIcon size={20} />}
        </BarButton>
        {actions}
      </TopBar>

      {query !== null ? (
        <label className="m-search in-bar">
          <SearchIcon />
          <input type="search" autoFocus aria-label={`Search cards in ${bucket?.name ?? 'no bucket'}`} placeholder="Search these cards" value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
      ) : (
        tabs.length > 1 && (
          <div className="m-tabs-bar">
            <nav className="m-tabs" aria-label="Buckets">
              {tabs.map((t) => (
                <button
                  key={t.id}
                  type="button"
                  className="m-tab"
                  aria-current={t.id === bucketId ? 'page' : undefined}
                  onClick={() => navigate(bucketPath(board.shortId, t.id), { replace: true })}
                >
                  {t.name}
                  <span className="m-tab-count">{bucketItems(board, t.id).filter((i) => i.kind === 'card').length}</span>
                </button>
              ))}
            </nav>
            {/* Which bucket of how many, centered under the tabs (which may scroll out of sight). */}
            <span className="m-dots" aria-hidden="true">
              {tabs.map((t) => (
                <span key={t.id} className={t.id === bucketId ? 'current' : undefined} />
              ))}
            </span>
          </div>
        )
      )}

      <div className="m-scroll m-cards">
        {items.map((item) =>
          item.kind === 'card' ? (
            <Card
              key={item.card.id}
              card={item.card}
              images={attached(item.card)}
              tagColors={tagColors}
              selected={false}
              lifted={false}
              highlight={terms}
              onExpand={() => navigate(item.card.path)}
            />
          ) : (
            <BoardImageView
              key={item.image.id}
              image={item.image}
              selected={false}
              lifted={false}
              onOpen={() => navigate(imagePath(board.shortId, item.image.id))}
            />
          ),
        )}
        {items.length === 0 && <p className="m-empty">{terms.length > 0 ? 'No card here matches.' : 'Nothing in this bucket yet.'}</p>}
      </div>

      {canAdd && query === null && <AddButton label="Card" onClick={() => void addCard()} />}
    </div>
  )
}
