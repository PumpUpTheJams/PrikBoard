import { useEffect, useLayoutEffect, useReducer, useRef, useState } from 'react'

/** Screen pixels kept between a panel and its item, and between it and the edge of the view. */
export const PANEL_GAP = 12
export const PANEL_MARGIN = 8
/** What floats over a canvas. Nothing on a zoomed canvas can be drawn above it, so a panel moves clear of it. */
const FLOATING = ':scope > .rail, :scope > .view-controls, :scope > .board-filters'

export interface Box {
  left: number
  top: number
  right: number
  bottom: number
}

export const overlaps = (a: Box, b: Box) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom
const moved = (box: Box, x: number, y: number): Box => ({ left: box.left + x, top: box.top + y, right: box.right + x, bottom: box.bottom + y })

/** How far a panel at `box` moves to clear what floats over the canvas: sideways if that stays in `view`, else up as far as there is room. */
export function clearOfFloating(box: Box, floating: Box[], view: Box) {
  let x = 0
  let y = 0
  for (let pass = 0; pass <= 2 * floating.length; pass++) {
    const at = moved(box, x, y)
    const hit = floating.find((f) => overlaps(at, f))
    if (!hit) break
    const sideways = [hit.left - PANEL_MARGIN - at.right, hit.right + PANEL_MARGIN - at.left]
      .filter((dx) => {
        const to = moved(at, dx, 0)
        return to.left >= view.left + PANEL_MARGIN && to.right <= view.right - PANEL_MARGIN && !floating.some((f) => overlaps(to, f))
      })
      .sort((a, b) => Math.abs(a) - Math.abs(b))[0]
    if (sideways !== undefined) {
      x += sideways
      continue
    }
    const up = Math.max(hit.top - PANEL_MARGIN - at.bottom, view.top + PANEL_MARGIN - at.top)
    if (up >= 0) break
    y += up
  }
  return { x, y: Math.max(y, view.top + PANEL_MARGIN - box.top) }
}

export const scrollerOf = (el: Element) => el.closest('.board-scroll, .card-scroll')
export const floatingOver = (scroller: Element) => [...scroller.parentElement!.querySelectorAll(FLOATING)].map((f) => f.getBoundingClientRect())

/**
 * Where something above a zoomed canvas goes, worked out from the page by `measure` after a render and a scroll or resize of the
 * canvas or of itself (scrolling doesn't render it). It settles because a place that stays the same renders nothing. With a `key`,
 * it only measures when that says something changed: measuring lays out the canvas, which costs a lot on a big board in the middle
 * of a drag.
 */
export function usePlaceOnCanvas<P extends Record<string, number | boolean>>(initial: P, measure: (el: HTMLDivElement, scroller: Element, place: P) => P, key?: string) {
  const ref = useRef<HTMLDivElement>(null)
  const [place, setPlace] = useState(initial)
  const [moves, moved] = useReducer((n: number) => n + 1, 0)
  const measured = useRef<string | null>(null)
  /** Moves made since the last frame: moving it can scroll the canvas (it counts in what scrolls), which measures it again. */
  const burst = useRef({ moves: 0, frame: 0 })
  useEffect(() => () => cancelAnimationFrame(burst.current.frame), [])
  useEffect(() => {
    const scroller = scrollerOf(ref.current!)
    if (!scroller) return
    scroller.addEventListener('scroll', moved, { passive: true })
    const observer = new ResizeObserver(moved)
    observer.observe(scroller)
    observer.observe(ref.current!)
    return () => {
      scroller.removeEventListener('scroll', moved)
      observer.disconnect()
    }
  }, [])
  const settle = (next: P) => {
    if (!Object.keys(next).some((name) => next[name] !== place[name])) return
    // A few moves in a frame settle it; more means two places keep sending it to each other, so it stays where it is until the next.
    const b = burst.current
    if (b.moves++ >= 6) return
    b.frame ||= requestAnimationFrame(() => {
      b.moves = 0
      b.frame = 0
    })
    setPlace(next)
  }
  useLayoutEffect(() => {
    const inputs = key === undefined ? null : `${key} ${moves} ${JSON.stringify(place)}`
    if (inputs !== null && inputs === measured.current) return
    measured.current = inputs
    const scroller = scrollerOf(ref.current!)
    if (scroller) settle(measure(ref.current!, scroller, place))
  })
  return [ref, place] as const
}
