import { useLayoutEffect, useState, type PointerEvent, type RefObject } from 'react'
import type { Rect } from '../api/types'

/** Where the expanded card's size and place are kept, per browser (not per card). Other windows pass their own key. */
const STORAGE_KEY = 'prikboard.expandedCard.window'
const MIN_WIDTH = 560
const MIN_HEIGHT = 420
/** Room left around a new window. */
const MARGIN = 40
/** Room always kept at the right and bottom: the board's scroll bars, and the window's resize edges just outside it. */
const EDGE = 20

export type WindowEdge = 'e' | 's' | 'se'

function stored(key: string): Rect | null {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? 'null') as Partial<Rect> | null
    return value && [value.x, value.y, value.width, value.height].every((n) => typeof n === 'number') ? (value as Rect) : null
  } catch {
    return null
  }
}

function store(key: string, rect: Rect) {
  try {
    localStorage.setItem(key, JSON.stringify(rect))
  } catch {
    // Storage can be unavailable (private windows, blocked site data); the window then just opens at the default.
  }
}

/** Keeps a rectangle inside a container of `width` by `height` (less the edge), at least the minimum size where it fits. */
function clamp(r: Rect, containerWidth: number, containerHeight: number): Rect {
  const width = containerWidth - EDGE
  const height = containerHeight - EDGE
  const w = Math.min(width, Math.max(MIN_WIDTH, r.width))
  const h = Math.min(height, Math.max(MIN_HEIGHT, r.height))
  return { x: Math.min(Math.max(0, r.x), width - w), y: Math.min(Math.max(0, r.y), height - h), width: w, height: h }
}

/**
 * A window floating over its offset parent (the board), moved by a press passed to `startMove` and resized from its right and
 * bottom edges. Its last size and place are remembered in localStorage. Null until the board has been measured.
 */
export function useFloatingWindow(windowRef: RefObject<HTMLElement | null>, storageKey = STORAGE_KEY) {
  const [rect, setRect] = useState<Rect | null>(null)
  /** Whether the window is being moved or resized right now. */
  const [moving, setMoving] = useState(false)

  useLayoutEffect(() => {
    const container = windowRef.current?.parentElement
    if (!container) return
    const fit = () => {
      const { clientWidth: width, clientHeight: height } = container
      setRect((current) => {
        const fallback = { width: Math.min(1200, width - 2 * MARGIN), height: height - 2 * MARGIN, x: 0, y: MARGIN }
        fallback.x = (width - fallback.width) / 2
        return clamp(current ?? stored(storageKey) ?? fallback, width, height)
      })
    }
    fit()
    const observer = new ResizeObserver(fit)
    observer.observe(container)
    return () => observer.disconnect()
  }, [windowRef, storageKey])

  /** Follows the pointer from a press until it is released, then remembers the result. */
  function track(e: PointerEvent, change: (start: Rect, dx: number, dy: number) => Rect) {
    const container = windowRef.current?.parentElement
    if (!rect || !container || e.button !== 0) return
    e.preventDefault()
    const start = rect
    const origin = { x: e.clientX, y: e.clientY }
    let last = start
    setMoving(true)
    const onMove = (ev: globalThis.PointerEvent) => {
      last = clamp(change(start, ev.clientX - origin.x, ev.clientY - origin.y), container.clientWidth, container.clientHeight)
      setRect(last)
    }
    const onUp = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', onUp)
      setMoving(false)
      store(storageKey, last)
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', onUp)
  }

  /** Moves the window, unless the press is on something in it that takes presses itself. */
  function startMove(e: PointerEvent) {
    if (e.target instanceof Element && e.target.closest('button, input, textarea, select, a')) return
    track(e, (s, dx, dy) => ({ ...s, x: s.x + dx, y: s.y + dy }))
  }

  const startResize = (edge: WindowEdge) => (e: PointerEvent) => {
    e.stopPropagation()
    track(e, (s, dx, dy) => ({ ...s, width: edge === 's' ? s.width : s.width + dx, height: edge === 'e' ? s.height : s.height + dy }))
  }

  return { rect, moving, startMove, startResize }
}
