import type { Point, Rect, ResizeHandle } from '../api/types'
import type { Selection, SelectionKind } from './boardItems'

/** A press that drags one item on the board: moving it, or resizing it from a corner or edge. */
export type ItemGesture = 'card' | 'bucket' | 'resize' | 'image' | 'image-resize' | 'drawing'

/** What a pointer gesture on the board does. */
export type GestureTarget =
  | {
      kind: ItemGesture
      id: string
      /** For resizes: the corner or edge being dragged. */
      handle?: ResizeHandle
    }
  /** Drawing a new bucket with the bucket tool. */
  | { kind: 'draw' }
  | {
      /** Moving several selected items at once. */
      kind: 'group'
      /** The item pressed. */
      id: string
      held: SelectionKind
      /** The items that move themselves (not those in a bucket that moves too: it carries them). */
      members: Selection[]
      /** Shaken cards lined up in a grid: where each goes, before the drag's distance is added. */
      grid?: Record<string, Point>
    }
  | {
      /** The selection rectangle. */
      kind: 'select'
      /** Inside this bucket it takes the bucket's own items; null: over the board. */
      bucketId: string | null
      /** The selection it adds to. */
      base: Selection[]
    }

/** A pointer gesture on the board, in canvas coordinates. */
export type Gesture = GestureTarget & {
  start: Point
  current: Point
  /** Finished: it stays until the board has reloaded, so the dropped item doesn't jump back while the request is in flight. */
  done?: boolean
  /** For a card drag: the place it took up when it began. Released back over it, the drag is called off (not for a group). */
  home?: Rect
  /** The pointer has left `home` since the drag began. */
  away?: boolean
}

export type GroupGesture = Extract<Gesture, { kind: 'group' }>
export type ItemDrag = Extract<Gesture, { kind: ItemGesture }>
