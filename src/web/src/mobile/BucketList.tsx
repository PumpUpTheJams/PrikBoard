import type { ReactNode } from 'react'
import type { BoardDetail } from '../api/types'
import { BucketIcon, ChevronRightIcon, SparkleIcon } from '../board/icons'
import { bucketPath, cardLabel, LOOSE } from '../board/links'
import { bucketItems, bucketsInOrder, hasLoose, itemCount } from './items'
import { navigate } from './nav'
import { TopBar } from './parts'

/** The second mobile screen: the board's buckets, to pick one. */
export function BucketList({ board, actions }: { board: BoardDetail; actions: ReactNode }) {
  const buckets = bucketsInOrder(board)

  return (
    <div className="m-screen">
      <TopBar up={{ to: '', label: 'Back to boards' }} title={board.name} context={<span className="board-key">{board.key}</span>}>
        {actions}
      </TopBar>

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
    </div>
  )
}
