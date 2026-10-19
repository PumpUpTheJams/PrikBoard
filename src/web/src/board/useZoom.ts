import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import type { Point } from '../api/types'
import { wheelZoom } from './zoom'

interface ZoomOptions {
  /** Called before every zoom change. */
  onZoom?: () => void
  /**
   * `always`: the wheel zooms (the board); Shift + wheel, or a mostly sideways swipe, scrolls instead.
   * `modifier` (default): only Ctrl/Cmd + wheel zooms, so the wheel still scrolls what is under it (a post-it's canvas).
   * A trackpad pinch counts as Ctrl + wheel either way.
   */
  wheel?: 'always' | 'modifier'
}

/** Wheel travel in pixels, whatever unit the browser reports it in. */
function wheelPixels(e: WheelEvent) {
  const pixels = e.deltaY * (e.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 : e.deltaMode === WheelEvent.DOM_DELTA_PAGE ? 800 : 1)
  // A trackpad pinch sends many small, fractional steps (as Ctrl + wheel); speed those up so pinching feels direct.
  return (e.ctrlKey || e.metaKey) && !Number.isInteger(e.deltaY) ? pixels * 5 : pixels
}

/**
 * Zoom for a scrolling canvas scaled with a CSS transform, whose top-left corner sits `inset` screen pixels right of and
 * below the scroll origin. The wheel zooms smoothly around the pointer (see `wheel`); `zoomTo` keeps a screen point
 * (default: the middle) in place.
 */
export function useZoom(scrollRef: RefObject<HTMLElement | null>, inset: Point, { onZoom, wheel = 'modifier' }: ZoomOptions = {}) {
  const [zoom, setZoom] = useState(1)
  /** A canvas point to keep under the same screen point once a new zoom level has rendered. */
  const anchor = useRef<{ canvas: Point; screen: Point } | null>(null)
  /** Wheel travel waiting for the next frame, where the pointer was last, and that frame. */
  const pending = useRef<{ travel: number; screen: Point } | null>(null)
  const frame = useRef(0)

  useLayoutEffect(() => {
    const a = anchor.current
    const el = scrollRef.current
    if (!a || !el) return
    anchor.current = null
    el.scrollLeft = a.canvas.x * zoom + inset.x - a.screen.x
    el.scrollTop = a.canvas.y * zoom + inset.y - a.screen.y
  }, [zoom, inset.x, inset.y, scrollRef])

  function zoomTo(next: number, screen?: Point) {
    const el = scrollRef.current
    if (next === zoom || !el) return
    const at = screen ?? { x: el.clientWidth / 2, y: el.clientHeight / 2 }
    anchor.current = { canvas: { x: (el.scrollLeft + at.x - inset.x) / zoom, y: (el.scrollTop + at.y - inset.y) / zoom }, screen: at }
    onZoom?.()
    setZoom(next)
  }

  /** The latest zoom and zoomTo, for the next animation frame. */
  const latest = useRef({ zoom, zoomTo })
  useLayoutEffect(() => {
    latest.current = { zoom, zoomTo }
  })
  useEffect(() => () => cancelAnimationFrame(frame.current), [])

  // React's wheel listener is passive, so register it by hand. Wheel events come faster than frames: their travel is
  // added up and applied once per frame, so the zoom and the scroll position stay in step.
  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    function onWheel(e: WheelEvent) {
      const zooms = e.ctrlKey || e.metaKey || (wheel === 'always' && !e.shiftKey && Math.abs(e.deltaY) >= Math.abs(e.deltaX))
      if (!zooms || e.deltaY === 0) return
      e.preventDefault()
      const rect = el!.getBoundingClientRect()
      const screen = { x: e.clientX - rect.left, y: e.clientY - rect.top }
      pending.current = { travel: (pending.current?.travel ?? 0) + wheelPixels(e), screen }
      frame.current ||= requestAnimationFrame(() => {
        frame.current = 0
        const p = pending.current
        pending.current = null
        if (p) latest.current.zoomTo(wheelZoom(latest.current.zoom, p.travel), p.screen)
      })
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [scrollRef, wheel])

  return { zoom, zoomTo }
}
