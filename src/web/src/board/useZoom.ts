import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import type { Point } from '../api/types'
import { MAX_ZOOM, MIN_ZOOM, wheelZoom } from './zoom'

interface ZoomOptions {
  /** Called before every zoom change. */
  onZoom?: () => void
  /**
   * `always`: the wheel zooms (the board); Shift + wheel, or a mostly sideways swipe, scrolls instead.
   * `modifier` (default): only Ctrl/Cmd + wheel zooms, so the wheel still scrolls what is under it (a card's canvas).
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
 * below the scroll origin. The wheel zooms smoothly around the pointer (see `wheel`), and on a touch screen two fingers
 * pan and pinch; `zoomTo` keeps a screen point (default: the middle) in place.
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

  /** Shows the canvas at `next`, with `canvas` under `screen`. */
  function zoomAt(next: number, canvas: Point, screen: Point) {
    const el = scrollRef.current
    if (!el) return
    if (next === zoom) {
      el.scrollLeft = canvas.x * zoom + inset.x - screen.x
      el.scrollTop = canvas.y * zoom + inset.y - screen.y
      return
    }
    anchor.current = { canvas, screen }
    onZoom?.()
    setZoom(next)
  }

  /** The latest zoom, inset, zoomTo and zoomAt, for the next animation frame and the touch listeners. */
  const latest = useRef({ zoom, inset, zoomTo, zoomAt })
  useLayoutEffect(() => {
    latest.current = { zoom, inset, zoomTo, zoomAt }
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

  // Two fingers pan and pinch. The second finger takes both pointers from whatever the first one started (a drag, a
  // selection rectangle, a pen stroke), which ends it as a lost pointer capture does; from then on the fingers only move
  // the view until the last one lifts. The listeners run first (capture phase), so nothing under the fingers sees them.
  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    const fingers = new Map<number, Point>()
    /** The canvas point under the middle of the two fingers as they went down, and the zoom and distance then. */
    let pinch: { canvas: Point; zoom: number; distance: number } | null = null
    /** A pinch began, and not every finger has lifted since. */
    let pinching = false

    const local = (e: PointerEvent) => {
      const rect = el!.getBoundingClientRect()
      return { x: e.clientX - rect.left, y: e.clientY - rect.top }
    }
    function twoFingers() {
      const [a, b] = [...fingers.values()]
      return { middle: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }, distance: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)) }
    }
    function beginPinch() {
      const { middle, distance } = twoFingers()
      const { zoom, inset } = latest.current
      const canvas = { x: (el!.scrollLeft + middle.x - inset.x) / zoom, y: (el!.scrollTop + middle.y - inset.y) / zoom }
      pinch = { canvas, zoom, distance }
    }

    function onDown(e: PointerEvent) {
      if (e.pointerType !== 'touch') return
      fingers.set(e.pointerId, local(e))
      if (fingers.size < 2 && !pinching) return
      e.stopPropagation()
      e.preventDefault()
      if (fingers.size < 2) return
      if (!pinching) {
        for (const id of fingers.keys()) {
          try {
            el!.setPointerCapture(id)
          } catch {
            // That finger has already lifted.
          }
        }
      }
      pinching = true
      beginPinch()
    }
    function onMove(e: PointerEvent) {
      if (!fingers.has(e.pointerId)) return
      fingers.set(e.pointerId, local(e))
      if (!pinching) return
      e.stopPropagation()
      if (!pinch || fingers.size < 2) return
      const { middle, distance } = twoFingers()
      const scaled = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, (pinch.zoom * distance) / pinch.distance))
      latest.current.zoomAt(Math.abs(scaled - 1) < 0.02 ? 1 : scaled, pinch.canvas, middle)
    }
    function onUp(e: PointerEvent) {
      if (!fingers.delete(e.pointerId)) return
      if (!pinching) return
      e.stopPropagation()
      pinch = null
      if (fingers.size >= 2) beginPinch()
      if (fingers.size === 0) pinching = false
    }

    const options = { capture: true }
    el.addEventListener('pointerdown', onDown, options)
    el.addEventListener('pointermove', onMove, options)
    el.addEventListener('pointerup', onUp, options)
    el.addEventListener('pointercancel', onUp, options)
    return () => {
      el.removeEventListener('pointerdown', onDown, options)
      el.removeEventListener('pointermove', onMove, options)
      el.removeEventListener('pointerup', onUp, options)
      el.removeEventListener('pointercancel', onUp, options)
    }
  }, [scrollRef])

  return { zoom, zoomTo }
}
