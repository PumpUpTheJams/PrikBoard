import type { CSSProperties } from 'react'
import { createPortal } from 'react-dom'
import type { CardColor, CardDetail, Point, Rect } from '../api/types'
import { presenceColor } from '../live/presence'
import type { LiveDrag } from '../live/useLive'
import { Card } from './Card'
import { CARD_HEIGHT, CARD_WIDTH } from './layout'
import type { TagColors } from './tagColors'

/** Where a card lands or was: an outline of its size at a canvas point. */
export function CardSlot({ at }: { at: Point }) {
  return <div className="card-slot" aria-hidden="true" style={{ left: at.x, top: at.y, width: CARD_WIDTH, height: CARD_HEIGHT }} />
}

interface CardGhostProps {
  card: CardDetail
  /** The pointer, in screen pixels. */
  client: Point
  /** Where on the card it was grabbed, in canvas pixels. */
  grab: Point
  zoom: number
  images: number | undefined
  tagColors: TagColors
}

/** A card dragged over a window that lies above the board (the open card, the trash), shown on top at the pointer. */
export function CardGhost({ card, client, grab, zoom, images, tagColors }: CardGhostProps) {
  return createPortal(
    <div
      className="drag-ghost card-ghost"
      aria-hidden="true"
      style={{ left: client.x - grab.x * zoom, top: client.y - grab.y * zoom, transform: `scale(${zoom})` }}
    >
      <Card card={card} point={{ x: 0, y: 0 }} images={images} tagColors={tagColors} selected={false} lifted onPointerDown={() => {}} onExpand={() => {}} />
    </div>,
    document.body,
  )
}

/** A new card carried from the tool rail: it follows the pointer (screen pixels), at the board's zoom. */
export function NewCardGhost({ client, fill, overTrash, zoom }: { client: Point; fill: CardColor; overTrash: boolean; zoom: number }) {
  return createPortal(
    <div
      className={`drag-ghost card-ghost${overTrash ? ' over-trash' : ''}`}
      aria-hidden="true"
      style={{ left: client.x - (CARD_WIDTH / 2) * zoom, top: client.y - (CARD_HEIGHT / 2) * zoom, transform: `scale(${zoom})` }}
    >
      <div className={`card lifted fill-${fill.toLowerCase()}`} style={{ width: CARD_WIDTH, height: CARD_HEIGHT }}>
        <div className="card-head">
          <span className="card-title" />
        </div>
      </div>
    </div>,
    document.body,
  )
}

/** Other people's drags in progress, outlined with their name: each of several items moving together, the name on the first. */
export function RemoteDragOutlines({ drags, rectsOf }: { drags: LiveDrag[]; rectsOf: (drag: LiveDrag) => Rect[] }) {
  return drags.flatMap((r) =>
    rectsOf(r).map((rect, i) => {
      const style = { left: rect.x, top: rect.y, width: rect.width, height: rect.height, '--who': presenceColor(r.principalId) }
      return (
        <div key={`${r.key}:${i}`} className="remote-drag" aria-hidden="true" style={style as CSSProperties}>
          {i === 0 && <span className="remote-drag-name">{r.displayName}</span>}
        </div>
      )
    }),
  )
}
