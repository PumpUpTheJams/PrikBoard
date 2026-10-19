import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import type { Point } from '../api/types'
import { stepZoom } from './zoom'

/**
 * Zoom for a scrolling canvas scaled with a CSS transform, whose left edge sits `inset` screen pixels right of the
 * scroll origin. Ctrl/Cmd + wheel zooms around the pointer; `zoomTo` keeps a screen point (default: the middle) in place.
 */
export function useZoom(scrollRef: RefObject<HTMLElement | null>, inset: number, onZoom?: () => void) {
  const [zoom, setZoom] = useState(1)
  /** A canvas point to keep under the same screen point once a new zoom level has rendered. */
  const anchor = useRef<{ canvas: Point; screen: Point } | null>(null)

  useLayoutEffect(() => {
    const a = anchor.current
    const el = scrollRef.current
    if (!a || !el) return
    anchor.current = null
    el.scrollLeft = a.canvas.x * zoom + inset - a.screen.x
    el.scrollTop = a.canvas.y * zoom - a.screen.y
  }, [zoom, inset, scrollRef])

  function zoomTo(next: number, screen?: Point) {
    const el = scrollRef.current
    if (next === zoom || !el) return
    const at = screen ?? { x: el.clientWidth / 2, y: el.clientHeight / 2 }
    anchor.current = { canvas: { x: (el.scrollLeft + at.x - inset) / zoom, y: (el.scrollTop + at.y) / zoom }, screen: at }
    onZoom?.()
    setZoom(next)
  }

  // React's wheel listener is passive, so register it by hand.
  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    function onWheel(e: WheelEvent) {
      if (!e.ctrlKey && !e.metaKey) return
      e.preventDefault()
      const rect = el!.getBoundingClientRect()
      zoomTo(stepZoom(zoom, e.deltaY < 0 ? 1 : -1), { x: e.clientX - rect.left, y: e.clientY - rect.top })
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  })

  return { zoom, zoomTo }
}
