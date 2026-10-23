import { useCallback, useEffect, useState } from 'react'
import type { Point, Rect } from '../api/types'
import { keyTargetIgnored } from './keys'

/** One selected item on a canvas. */
export interface Picked<K extends string> {
  kind: K
  id: string
}

export const isPicked = <K extends string>(list: Picked<K>[], kind: K, id: string) => list.some((p) => p.kind === kind && p.id === id)

/** Shift or Ctrl/Cmd + click: the item joins the selection, or leaves it when it was in it already. */
export function togglePicked<K extends string, P extends Picked<K>>(list: P[], item: P): P[] {
  return isPicked(list, item.kind, item.id) ? list.filter((p) => !(p.kind === item.kind && p.id === item.id)) : [...list, item]
}

/** Both selections together, each item once (a Shift-drag adds to what was selected). */
export function mergePicked<K extends string, P extends Picked<K>>(list: P[], more: P[]): P[] {
  return [...list, ...more.filter((p) => !isPicked(list, p.kind, p.id))]
}

/** Whether a press adds to the selection (Shift, or Ctrl/Cmd) rather than starting it afresh. */
export const adds = (e: { shiftKey: boolean; ctrlKey: boolean; metaKey: boolean }) => e.shiftKey || e.ctrlKey || e.metaKey

/** Whether two rectangles overlap: the selection rectangle takes what it touches. */
export const touches = (a: Rect, b: Rect) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height

/** Whether a point lies in a rectangle (its right and bottom edges are outside). */
export const contains = (r: Rect, p: Point) => p.x >= r.x && p.x < r.x + r.width && p.y >= r.y && p.y < r.y + r.height

/** Whether a screen point lies in an element's client rectangle (its right and bottom edges are outside). */
export const insideClient = (r: DOMRect, p: Point) => p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom

/** The smallest rectangle around all of them (points count as empty rectangles); null for none. */
export function bounds(rects: (Point & { width?: number; height?: number })[]): Rect | null {
  if (rects.length === 0) return null
  const x = Math.min(...rects.map((r) => r.x))
  const y = Math.min(...rects.map((r) => r.y))
  const right = Math.max(...rects.map((r) => r.x + (r.width ?? 0)))
  const bottom = Math.max(...rects.map((r) => r.y + (r.height ?? 0)))
  return { x, y, width: right - x, height: bottom - y }
}

/** A canvas's selected items: one, or several (Shift or Ctrl/Cmd + click, or the selection rectangle). */
export function useSelection<P extends Picked<string>>() {
  const [selected, setSelected] = useState<P[]>([])
  /** The one item selected, for what works on a single item (its panel, its handles); null with none or several. */
  const selection = selected.length === 1 ? selected[0] : null
  const setSelection = useCallback((item: P | null) => setSelected(item ? [item] : []), [])
  /** Takes an item out of the selection (it was deleted). */
  const unselect = useCallback(
    (id: string) => setSelected((list) => (list.some((s) => s.id === id) ? list.filter((s) => s.id !== id) : list)),
    [],
  )
  const isSelected = (kind: P['kind'], id: string) => isPicked<P['kind']>(selected, kind, id)
  return { selected, setSelected, selection, setSelection, unselect, isSelected }
}

/**
 * Whether Space is held down: a left press then pans the canvas, as the middle button does. Space pressed in a field,
 * button, menu or dialog is left to it.
 */
export function useSpaceHeld(): boolean {
  const [held, setHeld] = useState(false)
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.code !== 'Space' || e.ctrlKey || e.metaKey || e.altKey) return
      if (keyTargetIgnored(e.target, { editable: true, controls: true, inCard: true })) return
      // Space would scroll the board or the card otherwise.
      e.preventDefault()
      setHeld(true)
    }
    const onKeyUp = (e: KeyboardEvent) => e.code === 'Space' && setHeld(false)
    const release = () => setHeld(false)
    window.addEventListener('keydown', onKeyDown)
    window.addEventListener('keyup', onKeyUp)
    window.addEventListener('blur', release)
    return () => {
      window.removeEventListener('keydown', onKeyDown)
      window.removeEventListener('keyup', onKeyUp)
      window.removeEventListener('blur', release)
    }
  }, [])
  return held
}
