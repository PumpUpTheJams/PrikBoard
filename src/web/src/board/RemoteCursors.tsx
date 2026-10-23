import { useEffect, useState, useSyncExternalStore, type CSSProperties } from 'react'
import type { LiveViewer, Rect } from '../api/types'
import { Face } from '../components/Avatar'
import { presenceColor } from '../live/presence'
import type { CursorStore, LiveDrag } from '../live/useLive'
import { SparkleIcon } from './icons'
import { tacksOf } from './tacks'

/** A thumbtack that hasn't moved for this long fades: its owner is probably looking elsewhere. */
const IDLE_MS = 30_000

/**
 * The others' pointers on the board as push pins in their presence color with their picture beside it, and pinned
 * into what they drag. They keep their size on screen whatever the zoom. `focused`: the one picked in the top bar, which
 * pulses and shows its name. It follows the pointers by itself, so the board doesn't draw again as they move.
 */
export function RemoteCursors({ cursors, drags, viewers, heldRect, zoom, focused }: {
  cursors: CursorStore
  drags: LiveDrag[]
  viewers: LiveViewer[]
  heldRect: (drag: LiveDrag) => Rect | null
  zoom: number
  focused: string | null
}) {
  const list = useSyncExternalStore(cursors.subscribe, cursors.list)
  const tacks = tacksOf(list, drags, viewers, heldRect)
  const now = useNow(tacks.length > 0)
  return tacks.map(({ key, viewer, at, pinned, updatedAt }) => {
    const idle = !pinned && now - updatedAt > IDLE_MS
    const style = { left: at.x, top: at.y, '--who': presenceColor(viewer.principalId), '--unzoom': 1 / zoom } as CSSProperties
    return (
      <div
        key={key}
        className={`tack${pinned ? ' pricked' : ''}${idle ? ' idle' : ''}${focused === key ? ' focused' : ''}`}
        aria-hidden="true"
        style={style}
      >
        <span className="tack-shadow" />
        <span className="tack-dent" />
        <span className="tack-body">
          <span className="tack-pin">
            <PushPin />
          </span>
        </span>
        <span className={`avatar tack-head${viewer.kind === 'Agent' ? ' agent' : ''}`}>
          {viewer.kind === 'Agent' ? <SparkleIcon size={12} /> : <Face name={viewer.displayName} avatarUrl={viewer.avatarUrl} />}
        </span>
        {focused === key && <span className="tack-name">{viewer.displayName}</span>}
      </div>
    )
  })
}

/**
 * A push pin standing up, seen from the side and a little above (every cross-section the same flat ellipse), its
 * needle's tip at the bottom middle: a flat cap, a tapered barrel and a flared skirt in the person's color (`--who`),
 * lit from the left, and a steel needle out of the middle of the skirt.
 */
function PushPin() {
  return (
    <svg className="tack-svg" viewBox="0 0 36 70" width="36" height="70" aria-hidden="true">
      <defs>
        <linearGradient id="tack-shade" x1="0" x2="1" y1="0" y2="0">
          <stop offset="0" stopColor="#fff" stopOpacity="0.38" />
          <stop offset="0.35" stopColor="#fff" stopOpacity="0" />
          <stop offset="0.65" stopColor="#000" stopOpacity="0" />
          <stop offset="1" stopColor="#000" stopOpacity="0.38" />
        </linearGradient>
        <linearGradient id="tack-steel" x1="0" x2="1" y1="0" y2="0">
          <stop offset="0" stopColor="#f2f4f7" />
          <stop offset="0.5" stopColor="#b9bec7" />
          <stop offset="1" stopColor="#6d737d" />
        </linearGradient>
      </defs>
      <path className="tack-needle" d="M16.8 46h2.4l-0.7 21.5L18 70l-0.5-2.5z" fill="url(#tack-steel)" />
      <g className="tack-plastic">
        <path d="M12 31h12c1.5 5 8 8 8 13a14 4.2 0 0 1-28 0c0-5 6.5-8 8-13z" />
        <path d="M12 31h12c1.5 5 8 8 8 13a14 4.2 0 0 1-28 0c0-5 6.5-8 8-13z" fill="url(#tack-shade)" />
        <path d="M9 9h18l-3 23a6 1.8 0 0 1-12 0z" />
        <path d="M9 9h18l-3 23a6 1.8 0 0 1-12 0z" fill="url(#tack-shade)" />
        <path d="M11.6 13l2 17" stroke="#fff" strokeOpacity="0.45" strokeWidth="1.4" strokeLinecap="round" fill="none" />
        <path d="M5 6v4a13 4 0 0 0 26 0v-4z" />
        <path d="M5 6v4a13 4 0 0 0 26 0v-4z" fill="url(#tack-shade)" />
        <path d="M5 6v4a13 4 0 0 0 26 0v-4z" fill="#000" fillOpacity="0.15" />
        <ellipse cx="18" cy="6" rx="13" ry="4" />
        <ellipse cx="18" cy="6" rx="13" ry="4" fill="#fff" fillOpacity="0.22" />
      </g>
    </svg>
  )
}

/** The time, updated every few seconds while `on`, so idle thumbtacks fade without anything else changing. */
function useNow(on: boolean) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!on) return
    const timer = window.setInterval(() => setNow(Date.now()), 5000)
    return () => window.clearInterval(timer)
  }, [on])
  return now
}
