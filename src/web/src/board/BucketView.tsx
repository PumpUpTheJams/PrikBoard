import type { MouseEvent, PointerEvent } from 'react'
import type { BucketDetail, Rect, ResizeHandle } from '../api/types'
import { CardIcon, ChevronDownIcon, ChevronUpIcon, ImageIcon } from './icons'
import { collapsedRect } from './layout'
import { ResizeHandles } from './ResizeHandles'

/** How many cards and images are in a bucket, and how its label says that ("3 cards, 1 image"). */
function contentsOf(cards: number, images: number) {
  const cardsLabel = cards === 1 ? '1 card' : `${cards} cards`
  const imagesLabel = images === 1 ? '1 image' : `${images} images`
  return { cards, images, cardsLabel, imagesLabel, label: `${cardsLabel}${images > 0 ? `, ${imagesLabel}` : ''}` }
}

/** The cards and images in a bucket, each with its icon: a zero is left out, but an empty bucket shows 0 cards. */
function BucketCounts({ contents: { cards, images, cardsLabel, imagesLabel }, className }: { contents: ReturnType<typeof contentsOf>; className: string }) {
  return (
    <>
      {(cards > 0 || images === 0) && (
        <span className={className} title={cardsLabel}>
          <CardIcon size={12} />
          {cards}
        </span>
      )}
      {images > 0 && (
        <span className={className} title={imagesLabel}>
          <ImageIcon size={12} />
          {images}
        </span>
      )}
    </>
  )
}

const classes = (...names: (string | false)[]) => names.filter(Boolean).join(' ')

interface BucketViewProps {
  bucket: BucketDetail
  /** Where it is shown right now. */
  rect: Rect
  /** How many cards and images on the board are in it. */
  cards: number
  images: number
  /** Drawn above the others, with what is in it. */
  front: boolean
  dropTarget: boolean
  searchHit: boolean
  selected: boolean
  readOnly: boolean
  /** Shows its resize handles (it is the one item selected). */
  resizable: boolean
  /** A press inside, away from the rim: a drag there selects what is in it, a click selects the bucket. */
  onPress: (e: PointerEvent<HTMLElement>) => void
  /** A press on its name or its rim, which move it. */
  onGrab: (e: PointerEvent<HTMLElement>) => void
  /** Enter or Space on its name. */
  onPick: (e: MouseEvent) => void
  onToggle: () => void
  onResize: (e: PointerEvent<HTMLElement>, handle: ResizeHandle) => void
}

/** A bucket on the board: an outlined rectangle with its name and counts at the top. */
export function BucketView(props: BucketViewProps) {
  const { bucket, rect, front, dropTarget, searchHit, selected, readOnly, resizable, onPress, onGrab, onPick, onToggle, onResize } = props
  const contents = contentsOf(props.cards, props.images)
  return (
    <section
      aria-label={bucket.name}
      className={classes('bucket', front && 'in-front', dropTarget && 'drop-target', searchHit && 'search-hit', selected && 'selected')}
      style={{ left: rect.x, top: rect.y, width: rect.width, height: rect.height }}
      onPointerDown={onPress}
    >
      {(['n', 'e', 's', 'w'] as const).map((side) => (
        <span key={side} className={`bucket-rim ${side}`} aria-hidden="true" onPointerDown={onGrab} />
      ))}
      <button
        type="button"
        className="bucket-head"
        title="Drag to move the bucket and its cards"
        onPointerDown={onGrab}
        // A Shift or Ctrl/Cmd press that toggled the selection also ends in a click: onPick ignores it.
        onClick={onPick}
      >
        <span className="bucket-name">{bucket.name}</span>
        <span className="bucket-counts" aria-label={contents.label}>
          <BucketCounts contents={contents} className="count" />
        </span>
      </button>
      {!readOnly && (
        <button
          type="button"
          className="bucket-fold"
          aria-label={`Collapse ${bucket.name}`}
          title="Collapse the bucket: its cards fold away with it"
          onPointerDown={(e) => e.stopPropagation()}
          onClick={onToggle}
        >
          <ChevronUpIcon size={13} />
        </button>
      )}
      {!readOnly && resizable && <ResizeHandles onPointerDown={onResize} />}
    </section>
  )
}

interface CollapsedBucketViewProps {
  bucket: BucketDetail
  rect: Rect
  cards: number
  images: number
  dropTarget: boolean
  /** How many cards folded away in it match the search. */
  hits: number
  selected: boolean
  readOnly: boolean
  /** A press on it, which moves it. */
  onGrab: (e: PointerEvent<HTMLElement>) => void
  onToggle: () => void
}

/** A collapsed bucket: a square in its top-right corner with its name and what is inside. */
export function CollapsedBucketView({ bucket, rect, cards, images, dropTarget, hits, selected, readOnly, onGrab, onToggle }: CollapsedBucketViewProps) {
  const contents = contentsOf(cards, images)
  const square = collapsedRect(rect)
  const found = hits === 0 ? '' : hits === 1 ? '1 card inside matches the search' : `${hits} cards inside match the search`
  return (
    <section
      aria-label={`${bucket.name} (collapsed, ${contents.label}${found && `; ${found}`})`}
      data-fold={bucket.id}
      className={classes('bucket-collapsed', dropTarget && 'drop-target', hits > 0 && 'search-hit', selected && 'selected')}
      style={{ left: square.x, top: square.y, width: square.width, height: square.height }}
      title={found ? `${found}. Open the bucket to see them` : 'Drag to move the bucket'}
      onPointerDown={onGrab}
    >
      <span className={`bucket-collapsed-counts${cards > 0 && images > 0 ? ' both' : ''}`} aria-hidden>
        <BucketCounts contents={contents} className="bucket-collapsed-count" />
      </span>
      <span className="bucket-collapsed-name">{bucket.name}</span>
      {!readOnly && (
        <button
          type="button"
          className="bucket-fold"
          aria-label={`Open ${bucket.name}`}
          title="Open the bucket"
          onPointerDown={(e) => e.stopPropagation()}
          onClick={onToggle}
        >
          <ChevronDownIcon size={13} />
        </button>
      )}
    </section>
  )
}
