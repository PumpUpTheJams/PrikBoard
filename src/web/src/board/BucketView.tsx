import type { MouseEvent, PointerEvent } from 'react'
import type { BucketDetail, Rect, ResizeHandle } from '../api/types'
import { plural } from '../components/plural'
import { CardIcon, ChevronDownIcon, ChevronUpIcon, ImageIcon, PenIcon } from './icons'
import { collapsedRect } from './layout'
import { ResizeHandles } from './ResizeHandles'

/** How many items a bucket holds. */
export interface BucketCount {
  cards: number
  images: number
  drawings: number
}

/**
 * The counts a bucket shows, each with its icon and label: a zero is left out, but an empty bucket shows 0 cards. The
 * bucket's label joins them ("3 cards, 1 image").
 */
function contentsOf({ cards, images, drawings }: BucketCount) {
  const shown = [
    { kind: 'cards', n: cards, label: plural(cards, 'card', 'cards'), Icon: CardIcon },
    { kind: 'images', n: images, label: plural(images, 'image', 'images'), Icon: ImageIcon },
    { kind: 'drawings', n: drawings, label: plural(drawings, 'drawing', 'drawings'), Icon: PenIcon },
  ].filter((c) => c.n > 0 || (c.kind === 'cards' && images === 0 && drawings === 0))
  return { shown, label: shown.map((c) => c.label).join(', ') }
}

/** The counts of a bucket's items, each with its icon. */
function BucketCounts({ contents: { shown }, className }: { contents: ReturnType<typeof contentsOf>; className: string }) {
  return shown.map(({ kind, n, label, Icon }) => (
    <span key={kind} className={className} title={label}>
      <Icon size={12} />
      {n}
    </span>
  ))
}

const classes = (...names: (string | false)[]) => names.filter(Boolean).join(' ')

interface BucketViewProps {
  bucket: BucketDetail
  /** Where it is shown right now. */
  rect: Rect
  /** How many cards, and images and drawings on the board, are in it. */
  count: BucketCount
  /** Drawn above the others, with what is in it. */
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
  const { bucket, rect, dropTarget, searchHit, selected, readOnly, resizable, onPress, onGrab, onPick, onToggle, onResize } = props
  const contents = contentsOf(props.count)
  return (
    <section
      aria-label={bucket.name}
      className={classes('bucket', dropTarget && 'drop-target', searchHit && 'search-hit', selected && 'selected')}
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
  count: BucketCount
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
export function CollapsedBucketView({ bucket, rect, count, dropTarget, hits, selected, readOnly, onGrab, onToggle }: CollapsedBucketViewProps) {
  const contents = contentsOf(count)
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
      <span className={`bucket-collapsed-counts${contents.shown.length > 1 ? ' several' : ''}`} aria-hidden>
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
