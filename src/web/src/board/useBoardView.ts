import { useLayoutEffect, useRef, type RefObject } from 'react'
import type { Point, Rect } from '../api/types'
import { useScrollStore } from './canvasHooks'
import { reducedMotion } from './motion'
import { bounds, insideClient } from './selection'
import { useZoom } from './useZoom'

/** Empty canvas kept beyond the furthest item, so there is always room to drag things further out. */
const CANVAS_MARGIN = 600
/** Screen space left of the canvas, so the tool rail doesn't cover the left edge of the board. */
export const RAIL_INSET = 72
/** Room above the board while searching, so a card at the top can be scrolled below the list of results. */
export const SEARCH_MARGIN = 480

interface BoardViewOptions {
  scrollRef: RefObject<HTMLDivElement | null>
  canvasRef: RefObject<HTMLDivElement | null>
  searching: boolean
  /** Called before every zoom change. */
  onZoom: () => void
}

/**
 * The board's scroll viewport and zoom. Around the canvas is an empty `margin` of half the viewport on every side
 * (screen pixels), so any part of the board, its top-left corner too, can be scrolled to the middle of the screen.
 */
export function useBoardView({ scrollRef, canvasRef, searching, onZoom }: BoardViewOptions) {
  /** The scroll viewport, in screen pixels: its size as state, where it is scrolled to in `view` (a scroll renders nothing here). */
  const { size, view } = useScrollStore(scrollRef)
  // While searching there is more room above, so a card at the top can be scrolled below the list of results.
  const margin = { x: Math.round(size.width / 2), y: Math.round(Math.max(size.height / 2, searching ? SEARCH_MARGIN : 0)) }
  /** Where the canvas's top-left corner is in the scrolled area: the margin, plus room for the tool rail on the left. */
  const corner = { x: margin.x + RAIL_INSET, y: margin.y }
  // The wheel zooms the board (Shift + wheel scrolls).
  const { zoom, zoomTo } = useZoom(scrollRef, corner, { onZoom, wheel: 'always' })

  // When the margin changes (the first measurement, a resized window), the scroll position moves by as much, so what
  // is on screen stays where it is; a board opens with its top-left corner in view.
  const lastMargin = useRef({ x: 0, y: 0 })
  useLayoutEffect(() => {
    const el = scrollRef.current!
    el.scrollLeft += margin.x - lastMargin.current.x
    el.scrollTop += margin.y - lastMargin.current.y
    lastMargin.current = { x: margin.x, y: margin.y }
  }, [margin.x, margin.y, scrollRef])

  function toCanvas(e: { clientX: number; clientY: number }): Point {
    const rect = canvasRef.current!.getBoundingClientRect()
    return { x: (e.clientX - rect.left) / zoom, y: (e.clientY - rect.top) / zoom }
  }

  return {
    scrollRef,
    view,
    margin,
    corner,
    zoom,
    zoomTo,
    toCanvas,
    /** A screen point in canvas pixels when it is over the board's viewport (open windows included); else null. */
    boardPoint(client: Point): Point | null {
      return insideClient(scrollRef.current!.getBoundingClientRect(), client) ? toCanvas({ clientX: client.x, clientY: client.y }) : null
    },
  }
}

/**
 * The board's canvas: it has no edge, so it reaches from its leftmost and topmost item (or 0,0; `low`) to past the
 * furthest one, and `origin` is where 0,0 sits in the scrolled area. `rects`: where everything is shown right now.
 */
export function useCanvasExtent({ scrollRef, view, corner, zoom }: ReturnType<typeof useBoardView>, rects: Rect[]) {
  // Items left of or above 0,0 grow the canvas that way, while they are dragged there too.
  const all = bounds(rects)
  const low = { x: Math.min(0, all?.x ?? 0), y: Math.min(0, all?.y ?? 0) }
  const origin = { x: corner.x - low.x * zoom, y: corner.y - low.y * zoom }
  const extent = {
    width: CANVAS_MARGIN + Math.max(0, all ? all.x + all.width : 0),
    height: CANVAS_MARGIN + Math.max(0, all ? all.y + all.height : 0),
  }

  // When the canvas grows or shrinks on the left or top, the scroll position moves by as much, so what is on screen
  // (a card being dragged there too) stays where it is.
  const lastLow = useRef(low)
  useLayoutEffect(() => {
    const el = scrollRef.current!
    el.scrollLeft += (lastLow.current.x - low.x) * zoom
    el.scrollTop += (lastLow.current.y - low.y) * zoom
    lastLow.current = { x: low.x, y: low.y }
  }, [low.x, low.y, zoom, scrollRef])

  return {
    low,
    origin,
    extent,
    /** The middle of what is on screen, in canvas pixels. */
    viewportCenter(): Point {
      const v = view.get()
      return { x: (v.x - origin.x + v.width / 2) / zoom, y: (v.y - origin.y + v.height / 2) / zoom }
    },
    /**
     * All of the board, and the part on screen for the minimap to follow by itself (`view`, in screen pixels; `viewport`
     * turns it into canvas pixels).
     */
    minimap: {
      extent: { x: low.x, y: low.y, width: extent.width - low.x, height: extent.height - low.y },
      view,
      viewport: (v: Rect): Rect => ({ x: (v.x - origin.x) / zoom, y: (v.y - origin.y) / zoom, width: v.width / zoom, height: v.height / zoom }),
    },
    /** Scrolls so a canvas point (picked on the minimap) is in the middle of the screen; `smooth` glides there. */
    navigate(center: Point, { smooth = false } = {}) {
      const el = scrollRef.current!
      const to = { left: center.x * zoom + origin.x - el.clientWidth / 2, top: center.y * zoom + origin.y - el.clientHeight / 2 }
      el.scrollTo({ ...to, behavior: smooth && !reducedMotion() ? 'smooth' : 'auto' })
    },
  }
}
