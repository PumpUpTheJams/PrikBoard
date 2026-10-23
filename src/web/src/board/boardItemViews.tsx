import { memo, type MouseEvent, type PointerEvent } from 'react'
import type { BucketDetail, CardDetail, DrawingDetail, ImageDetail, Point, Rect, ResizeHandle } from '../api/types'
import type { ItemGesture } from './boardGesture'
import { BoardImageView } from './BoardImageView'
import { BucketView, CollapsedBucketView, type BucketCount } from './BucketView'
import { Card } from './Card'
import { CARD_HEIGHT, CARD_WIDTH } from './layout'
import { cardLabel } from './links'
import type { Reshape } from './drawings'
import { DrawingView } from './DrawingView'
import type { SearchTerm } from './search'
import type { TagColors } from './tagColors'

// The board's items, each memoized: the board renders on every pointer move of a drag, every zoom step and every
// reload, and with these only the items whose props changed render again. Their props are data and flags, plus one
// set of handlers that keeps its identity (useStableHandlers in BoardCanvas); geometry that didn't change is the
// item itself (liveGeometry returns it), so it compares equal too.

/** What the board's items call back, by kind and id. */
export interface BoardItemHandlers {
  /** A press on an item, or on one of its resize handles. */
  pointerDown: (e: PointerEvent<HTMLElement>, kind: ItemGesture, id: string, options?: { handle?: ResizeHandle }) => void
  expand: (cardId: string) => void
  renameTitle: (card: CardDetail, title: string) => Promise<boolean>
  titleDone: () => void
  quickEdit: (cardId: string, kind: 'tags' | 'people', anchor: DOMRect) => void
  deleteImage: (imageId: string) => void
  reshapeDrawing: (drawing: DrawingDetail, reshape: Reshape) => Promise<unknown>
  /** A press inside a bucket, away from its rim. */
  bucketPress: (e: PointerEvent<HTMLElement>, bucket: BucketDetail) => void
  pickBucket: (e: MouseEvent, bucket: BucketDetail) => void
  toggleBucket: (bucket: BucketDetail) => void
}

interface BoardCardProps {
  card: CardDetail
  point: Point
  images: number | undefined
  tagColors: TagColors
  selected: boolean
  lifted: boolean
  front: boolean
  dimmed: boolean
  highlight: SearchTerm[] | undefined
  current: boolean
  dropTarget: boolean
  editingTitle: boolean
  titleCaret: number | undefined
  readOnly: boolean
  /** The board is zoomed far out (below FAR_ZOOM): only the fill, title and tag colors show. */
  far: boolean
  handlers: BoardItemHandlers
}

/** A card on the board. */
export const BoardCard = memo(function BoardCard({ card, readOnly, far, handlers: h, ...shown }: BoardCardProps) {
  if (far) return <FarCard card={card} {...shown} onPointerDown={(e) => h.pointerDown(e, 'card', card.id)} onExpand={() => h.expand(card.id)} />
  return (
    <Card
      card={card}
      {...shown}
      onPointerDown={(e) => h.pointerDown(e, 'card', card.id)}
      onExpand={() => h.expand(card.id)}
      onRenameTitle={readOnly ? undefined : (title) => h.renameTitle(card, title)}
      onTitleDone={h.titleDone}
      onEditTags={readOnly ? undefined : (anchor) => h.quickEdit(card.id, 'tags', anchor)}
      onEditAssignees={readOnly ? undefined : (anchor) => h.quickEdit(card.id, 'people', anchor)}
    />
  )
})

type FarCardProps = Pick<BoardCardProps, 'card' | 'point' | 'tagColors' | 'selected' | 'lifted' | 'front' | 'dimmed' | 'current' | 'dropTarget'> & {
  onPointerDown: (e: PointerEvent<HTMLDivElement>) => void
  onExpand: () => void
}

/**
 * A card when the board is zoomed far out: its fill, title and a mark in each tag's color, without buttons, avatars or
 * shadows, so a whole board of them stays light. It is pressed, dragged and opened like any card; its title can't be
 * edited in place, as it is too small there to aim at.
 */
function FarCard({ card, point, tagColors, selected, lifted, front, dimmed, current, dropTarget, onPointerDown, onExpand }: FarCardProps) {
  const name = cardLabel(card)
  const classes = ['card', 'far', `fill-${card.color.toLowerCase()}`]
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (front) classes.push('in-front')
  if (card.agentWorking) classes.push('agent-working')
  if (dimmed) classes.push('dimmed')
  if (current) classes.push('search-current')
  if (dropTarget) classes.push('drop-target')
  return (
    <div
      className={classes.join(' ')}
      style={{ left: point.x, top: point.y, width: CARD_WIDTH, height: CARD_HEIGHT }}
      role="group"
      data-card={card.id}
      aria-label={card.agentWorking ? `${name} (agent working)` : name}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={onExpand}
      onKeyDown={(e) => e.key === 'Enter' && e.target === e.currentTarget && onExpand()}
    >
      <span className="card-far-title">{name}</span>
      {card.tags.length > 0 && (
        <span className="card-far-tags" aria-hidden="true">
          {card.tags.map((tag) => {
            const color = tagColors.get(tag)
            return <span key={tag} className={`card-far-tag${color && color !== 'Default' ? ` tag-color-${color.toLowerCase()}` : ''}`} />
          })}
        </span>
      )}
    </div>
  )
}

interface BoardImageProps {
  image: ImageDetail
  rect: Rect
  selected: boolean
  lifted: boolean
  front: boolean
  /** It is the one item selected, so it shows its resize handles (not for viewers). */
  resizable: boolean
  readOnly: boolean
  handlers: BoardItemHandlers
}

/** An image on the board. */
export const BoardImage = memo(function BoardImage({ image, rect, selected, lifted, front, resizable, readOnly, handlers: h }: BoardImageProps) {
  return (
    <BoardImageView
      image={image}
      rect={rect}
      selected={selected}
      lifted={lifted}
      front={front}
      onPointerDown={(e) => h.pointerDown(e, 'image', image.id)}
      // One of several selected has no handles: they move together, but resize one at a time.
      onResizePointerDown={resizable ? (e, handle) => h.pointerDown(e, 'image-resize', image.id, { handle }) : undefined}
      onDelete={readOnly ? undefined : () => h.deleteImage(image.id)}
    />
  )
})

interface BoardDrawingProps {
  drawing: DrawingDetail
  rect: Rect
  selected: boolean
  lifted: boolean
  front: boolean
  /** The board's zoom, while it is the one item selected and can be reshaped (its handles need it); else undefined. */
  reshapeZoom: number | undefined
  handlers: BoardItemHandlers
}

/** A drawing on the board. */
export const BoardDrawing = memo(function BoardDrawing({ drawing, rect, selected, lifted, front, reshapeZoom, handlers: h }: BoardDrawingProps) {
  return (
    <DrawingView
      drawing={drawing}
      rect={rect}
      selected={selected}
      lifted={lifted}
      front={front}
      onPointerDown={(e) => h.pointerDown(e as PointerEvent<HTMLElement>, 'drawing', drawing.id)}
      zoom={reshapeZoom}
      onReshape={reshapeZoom === undefined ? undefined : (reshape) => h.reshapeDrawing(drawing, reshape)}
    />
  )
})

interface BoardBucketProps {
  bucket: BucketDetail
  rect: Rect
  count: BucketCount
  front: boolean
  dropTarget: boolean
  searchHit: boolean
  selected: boolean
  readOnly: boolean
  resizable: boolean
  handlers: BoardItemHandlers
}

/** An open bucket on the board. */
export const BoardBucket = memo(function BoardBucket({ bucket, handlers: h, ...shown }: BoardBucketProps) {
  return (
    <BucketView
      bucket={bucket}
      {...shown}
      onPress={(e) => h.bucketPress(e, bucket)}
      onGrab={(e) => h.pointerDown(e, 'bucket', bucket.id)}
      onPick={(e) => h.pickBucket(e, bucket)}
      onToggle={() => h.toggleBucket(bucket)}
      onResize={(e, handle) => h.pointerDown(e, 'resize', bucket.id, { handle })}
    />
  )
})

interface BoardCollapsedBucketProps {
  bucket: BucketDetail
  rect: Rect
  count: BucketCount
  dropTarget: boolean
  hits: number
  selected: boolean
  readOnly: boolean
  handlers: BoardItemHandlers
}

/** A collapsed bucket on the board. */
export const BoardCollapsedBucket = memo(function BoardCollapsedBucket({ bucket, handlers: h, ...shown }: BoardCollapsedBucketProps) {
  return <CollapsedBucketView bucket={bucket} {...shown} onGrab={(e) => h.pointerDown(e, 'bucket', bucket.id)} onToggle={() => h.toggleBucket(bucket)} />
})
