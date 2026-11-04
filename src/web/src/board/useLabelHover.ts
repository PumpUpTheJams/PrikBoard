import { useCallback, useRef, useState } from 'react'

/** How long the bar of a label stays after the pointer leaves it, to get across the gap to the bar. */
const LEAVE_MS = 200

/** The label the pointer is over, which shows its bar without being selected. */
export function useLabelHover() {
  const [hovered, setHovered] = useState<string | null>(null)
  const timer = useRef(0)
  const keep = useCallback(() => window.clearTimeout(timer.current), [])
  const leave = useCallback(() => {
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setHovered(null), LEAVE_MS)
  }, [])
  const hover = useCallback((id: string, over: boolean) => {
    if (!over) {
      leave()
      return
    }
    window.clearTimeout(timer.current)
    setHovered(id)
  }, [leave])
  return { hovered, hover, barHover: { onPointerEnter: keep, onPointerLeave: leave } }
}
