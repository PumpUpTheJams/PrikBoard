import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

/** How long the pointer rests on something before its tooltip shows (ms). */
export const TOOLTIP_DELAY = 200
/** Space between the tooltip and what it belongs to, and the screen's edges. */
const GAP = 8

interface Tip {
  text: string
  /** Where the element it belongs to is, on screen. */
  anchor: DOMRect
}

/**
 * Tooltips for every `title` in the app, shown after TOOLTIP_DELAY instead of the browser's own delay, which is longer
 * and can't be changed. While the pointer is on an element, its title waits in `data-title`, so the browser shows
 * nothing itself; it goes back when the pointer leaves. Not for touch, which has no hover.
 */
export function Tooltips() {
  const [tip, setTip] = useState<Tip | null>(null)

  useEffect(() => {
    /** The element the pointer is on whose title is held back. */
    let held: Element | null = null
    let timer = 0

    function release() {
      window.clearTimeout(timer)
      setTip(null)
      if (!held) return
      const text = held.getAttribute('data-title')
      held.removeAttribute('data-title')
      // A re-render may have given it a new title meanwhile; that one wins.
      if (text !== null && !held.hasAttribute('title')) held.setAttribute('title', text)
      held = null
    }

    function onOver(e: PointerEvent) {
      if (e.pointerType === 'touch' || !(e.target instanceof Element)) return
      if (held?.contains(e.target)) return
      release()
      const el = e.target.closest('[title]')
      const text = el?.getAttribute('title')?.trim()
      if (!el || !text) return
      held = el
      el.setAttribute('data-title', el.getAttribute('title')!)
      el.removeAttribute('title')
      timer = window.setTimeout(() => {
        if (held === el && el.isConnected) setTip({ text, anchor: el.getBoundingClientRect() })
      }, TOOLTIP_DELAY)
    }

    function onOut(e: PointerEvent) {
      if (!held) return
      const to = e.relatedTarget
      if (!(to instanceof Node) || !held.contains(to)) release()
    }

    // A press, a key, scrolling or zooming hides it until the pointer moves on, as the browser's own does.
    function hide() {
      window.clearTimeout(timer)
      setTip(null)
    }

    document.addEventListener('pointerover', onOver)
    document.addEventListener('pointerout', onOut)
    document.addEventListener('pointerdown', hide, true)
    document.addEventListener('keydown', hide, true)
    document.addEventListener('wheel', hide, { capture: true, passive: true })
    document.addEventListener('scroll', hide, true)
    window.addEventListener('blur', release)
    return () => {
      release()
      document.removeEventListener('pointerover', onOver)
      document.removeEventListener('pointerout', onOut)
      document.removeEventListener('pointerdown', hide, true)
      document.removeEventListener('keydown', hide, true)
      document.removeEventListener('wheel', hide, true)
      document.removeEventListener('scroll', hide, true)
      window.removeEventListener('blur', release)
    }
  }, [])

  return tip ? <TooltipBox key={tip.text + tip.anchor.x + tip.anchor.y} tip={tip} /> : null
}

/** Below what it belongs to and centered on it, kept on screen; above it when there is no room below. */
function TooltipBox({ tip }: { tip: Tip }) {
  const ref = useRef<HTMLDivElement>(null)
  const [place, setPlace] = useState<{ left: number; top: number } | null>(null)

  useLayoutEffect(() => {
    const box = ref.current!.getBoundingClientRect()
    const { anchor } = tip
    const left = Math.min(Math.max(GAP, anchor.left + anchor.width / 2 - box.width / 2), window.innerWidth - box.width - GAP)
    const below = anchor.bottom + GAP
    const top = below + box.height + GAP <= window.innerHeight ? below : Math.max(GAP, anchor.top - GAP - box.height)
    setPlace({ left, top })
  }, [tip])

  return createPortal(
    <div ref={ref} className="tooltip" role="tooltip" style={place ?? { left: 0, top: 0, visibility: 'hidden' }}>
      {tip.text}
    </div>,
    document.body,
  )
}
