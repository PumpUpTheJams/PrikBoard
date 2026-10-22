import { useEffect, useState } from 'react'
import type { Point, Rect } from '../api/types'

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

/**
 * Whether Space is held down: a left press then pans the canvas, as the middle button does. Fields, buttons, menus and
 * dialogs keep the key.
 */
export function useSpaceHeld(): boolean {
  const [held, setHeld] = useState(false)
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.code !== 'Space' || e.ctrlKey || e.metaKey || e.altKey) return
      const target = e.target instanceof Element ? e.target : null
      if (target?.closest('input, textarea, select, button, a, [contenteditable="true"], [role="menu"], [role="dialog"]:not(.expanded), [role="alertdialog"]')) return
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
