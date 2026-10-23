import { useState, type PointerEvent } from 'react'
import type { Point } from '../api/types'
import { isDrag } from './gesture'
import { CARD_HEIGHT, CARD_WIDTH, snapPoint } from './layout'

/** Where a new card carried from the rail goes: centered on a canvas point (the pointer holds it there), snapped. */
const cardSpot = (p: Point) => snapPoint({ x: p.x - CARD_WIDTH / 2, y: p.y - CARD_HEIGHT / 2 })

interface NewCardDragOptions {
  readOnly: boolean
  /** Whether a screen point is over the rail's trash can. */
  onTrash: (client: Point) => boolean
  /** Shows the pointer over the trash can, or not. */
  setOverTrash: (over: boolean) => void
  /** Where a new card would go at a screen point, in canvas pixels: null when it isn't over the board. */
  landing: (client: Point) => Point | null
  /** A drag from the rail began. */
  onCarry: () => void
  /** Adds the new card with its top-left at a canvas point. */
  stick: (at: Point) => void
}

/**
 * A new card dragged from the rail's card button, added where it is released; without a drag the press stays a click,
 * which picks the card tool. Escape, or releasing it off the board or back on the button (a trash can by then), discards it.
 */
export function useNewCardDrag({ readOnly, onTrash, setOverTrash, landing, onCarry, stick }: NewCardDragOptions) {
  /** The pointer while a new card is carried, in screen pixels. */
  const [carried, setCarried] = useState<Point | null>(null)
  const at = carried && landing(carried)

  function onPointerDown(e: PointerEvent<HTMLButtonElement>) {
    if (e.button !== 0 || readOnly) return
    e.preventDefault()
    const start = { x: e.clientX, y: e.clientY }
    let dragged = false
    const end = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', end)
      window.removeEventListener('keydown', onKey, true)
      setCarried(null)
      setOverTrash(false)
    }
    const onMove = (ev: globalThis.PointerEvent) => {
      const current = { x: ev.clientX, y: ev.clientY }
      if (!dragged && !isDrag({ start, current })) return
      dragged = true
      onCarry()
      setCarried(current)
      setOverTrash(onTrash(current))
    }
    const onUp = (ev: globalThis.PointerEvent) => {
      end()
      if (!dragged) return
      // A drag isn't a click: the click that may follow on the button doesn't pick the tool.
      const swallow = (c: MouseEvent) => c.stopPropagation()
      window.addEventListener('click', swallow, { capture: true, once: true })
      setTimeout(() => window.removeEventListener('click', swallow, true))
      const spot = landing({ x: ev.clientX, y: ev.clientY })
      if (spot) stick(cardSpot(spot))
    }
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key !== 'Escape') return
      ev.stopPropagation()
      end()
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', end)
    window.addEventListener('keydown', onKey, true)
  }

  return {
    carried,
    /** Where the carried card would land, on the canvas. */
    spot: at ? cardSpot(at) : null,
    onPointerDown,
  }
}
