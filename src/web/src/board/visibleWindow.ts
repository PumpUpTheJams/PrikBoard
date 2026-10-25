import { startTransition, useLayoutEffect, useRef, useState } from 'react'
import type { Point, Rect } from '../api/types'
import type { ScrollView } from './canvasHooks'

/** The window around the screen is rounded out to this many canvas pixels, so it only changes now and then while scrolling. */
const STEP = 500

/**
 * The part of the board worth putting on the page: what is on screen and a screen's width and height around it on
 * every side, in canvas pixels, rounded out to STEP. Items outside it can stay off the page (BoardCanvas), so a big
 * board has a few hundred elements on the page instead of tens of thousands. `origin` is where 0,0 sits in the scrolled
 * area. While the window on the page still covers the screen (a scroll, a zoom step), the next one comes in a
 * transition, so the items it adds don't hold up the scroll or the zoom; after a jump (the minimap, a search result) it
 * comes at once.
 */
export function useVisibleWindow(view: ScrollView, zoom: number, origin: Point): Rect {
  const [key, setKey] = useState(() => windowOf(view.get(), zoom, origin).key)
  const shown = useRef(key)
  // The origin by its coordinates: the object is new every render.
  const { x: originX, y: originY } = origin
  useLayoutEffect(() => {
    function update() {
      const next = windowOf(view.get(), zoom, { x: originX, y: originY })
      if (next.key === shown.current) return
      const covered = contains(fromKey(shown.current), next.screen)
      shown.current = next.key
      if (covered) startTransition(() => setKey(next.key))
      else setKey(next.key)
    }
    update()
    return view.subscribe(update)
  }, [view, zoom, originX, originY])
  return fromKey(key)
}

/** The screen in canvas pixels, and the window around it as a string, so the same window compares equal. */
function windowOf(view: Rect, zoom: number, origin: Point): { key: string; screen: Rect } {
  const screen = { x: (view.x - origin.x) / zoom, y: (view.y - origin.y) / zoom, width: view.width / zoom, height: view.height / zoom }
  const left = screen.x - screen.width
  const top = screen.y - screen.height
  const down = (n: number) => Math.floor(n / STEP) * STEP
  const up = (n: number) => Math.ceil(n / STEP) * STEP
  return { key: `${down(left)} ${down(top)} ${up(left + 3 * screen.width)} ${up(top + 3 * screen.height)}`, screen }
}

function fromKey(key: string): Rect {
  const [x, y, right, bottom] = key.split(' ').map(Number)
  return { x, y, width: right - x, height: bottom - y }
}

const contains = (outer: Rect, inner: Rect) =>
  inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.width <= outer.x + outer.width && inner.y + inner.height <= outer.y + outer.height
