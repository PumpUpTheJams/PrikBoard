import { useState } from 'react'

/** How long the bar of a label stays after the pointer leaves it, to get across the gap to the bar. */
const LEAVE_MS = 200

/** The label the pointer is over, outside React state: only its bar renders as the mouse moves, not the whole canvas. */
export interface LabelHover {
  subscribe: (listener: () => void) => () => void
  get: () => string | null
  hover: (id: string, over: boolean) => void
  barHover: { onPointerEnter: () => void; onPointerLeave: () => void }
}

function hoverStore(): LabelHover {
  let hovered: string | null = null
  let timer = 0
  const listeners = new Set<() => void>()
  const set = (id: string | null) => {
    if (id === hovered) return
    hovered = id
    for (const listener of listeners) listener()
  }
  const keep = () => window.clearTimeout(timer)
  const leave = () => {
    keep()
    timer = window.setTimeout(() => set(null), LEAVE_MS)
  }
  return {
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    get: () => hovered,
    hover(id, over) {
      if (!over) return leave()
      keep()
      set(id)
    },
    barHover: { onPointerEnter: keep, onPointerLeave: leave },
  }
}

export function useLabelHover(): LabelHover {
  const [store] = useState(hoverStore)
  return store
}
