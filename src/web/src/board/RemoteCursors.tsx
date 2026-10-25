import { memo, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties } from 'react'
import type { LiveViewer, Point, Rect } from '../api/types'
import { Face } from '../components/Avatar'
import { presenceColor } from '../live/presence'
import type { CursorStore, LiveDrag, LiveSelection } from '../live/useLive'
import { SparkleIcon } from './icons'
import { headsOf, pinsOf, type Pin, type PinPlace } from './peers'

/** A head that hasn't moved for this long fades: its owner is probably looking elsewhere. */
const IDLE_MS = 30_000
/** How long a pin takes to pull out (`.peer-pin.out` in index.css). */
const PIN_OUT_MS = 140

/**
 * The others on the board: the head of each one's pointer (their picture in a ring of their presence color; hidden
 * while they drag, as the dragged item shows where they are), and a pin pricked into the top of everything they have
 * selected, which drops in when they select it and pulls out when they don't any more. Both keep their size on screen
 * whatever the zoom. `focused`: the connection picked in the top bar, whose head (or else pins) pulses and shows the
 * name. It follows the store by itself, so the board doesn't draw again as pointers move. `near`: the part of the board
 * on the page (useVisibleWindow); only the pins in it are drawn, since someone may have selected a thousand items.
 */
export function RemoteCursors({ cursors, viewers, drags, placeOf, near, zoom, focused }: {
  cursors: CursorStore
  viewers: LiveViewer[]
  drags: LiveDrag[]
  placeOf: (id: string) => PinPlace | null
  near: Rect
  zoom: number
  focused: string | null
}) {
  const peers = useSyncExternalStore(cursors.subscribe, cursors.snapshot)
  const dragging = new Set(drags.filter((d) => !d.endedAt).map((d) => d.key))
  const heads = headsOf(peers.cursors, viewers).filter((h) => !dragging.has(h.key))
  const now = useNow(heads.length > 0)
  const headFocused = heads.some((h) => h.key === focused)
  return (
    <>
      <Pins
        selections={peers.selections}
        viewers={viewers}
        placeOf={placeOf}
        near={near}
        zoom={zoom}
        focused={headFocused ? null : focused}
      />
      {heads.map(({ key, viewer, at, updatedAt }) => (
        <div
          key={key}
          className={`peer-pointer${now - updatedAt > IDLE_MS ? ' idle' : ''}${focused === key ? ' focused' : ''}`}
          aria-hidden="true"
          style={styleOf(viewer, at, zoom)}
        >
          <PeerFace viewer={viewer} />
          {focused === key && <span className="peer-name">{viewer.displayName}</span>}
        </div>
      ))}
    </>
  )
}

const styleOf = (viewer: LiveViewer, at: Point, zoom: number) =>
  ({ left: at.x, top: at.y, '--who': presenceColor(viewer.principalId), '--unzoom': 1 / zoom }) as CSSProperties

/**
 * The pins of the others' selections. Memoized apart from the heads, as the selections stay the same object while
 * pointers move; each pin is memoized too, so a drag or a zoom step only draws again the pins it moves.
 */
const Pins = memo(function Pins({ selections, viewers, placeOf, near, zoom, focused }: {
  selections: LiveSelection[]
  viewers: LiveViewer[]
  placeOf: (id: string) => PinPlace | null
  near: Rect
  zoom: number
  focused: string | null
}) {
  const pins = useLeaving(pinsOf({ cursors: [], selections }, viewers, placeOf, zoom), PIN_OUT_MS)
  return pins
    .filter(({ at }) => at.x >= near.x && at.x <= near.x + near.width && at.y >= near.y && at.y <= near.y + near.height)
    .map(({ id, key, viewer, at, out }) => (
      <PinView key={id} viewer={viewer} x={at.x} y={at.y} zoom={zoom} out={!!out} focused={focused === key} />
    ))
})

/** One pin, pricked in at (x, y). */
const PinView = memo(function PinView({ viewer, x, y, zoom, out, focused }: {
  viewer: LiveViewer
  x: number
  y: number
  zoom: number
  out: boolean
  focused: boolean
}) {
  return (
    <div className={`peer-pin${out ? ' out' : ''}${focused ? ' focused' : ''}`} aria-hidden="true" style={styleOf(viewer, { x, y }, zoom)}>
      <span className="peer-pin-hole" />
      <span className="peer-pin-clip">
        <span className="peer-pin-move">
          <svg className="peer-pin-needle" viewBox="0 0 5.6 20" width="5.6" height="20">
            <path d="M0 0h5.6L3.05 18.75 2.8 20l-.25-1.25z" />
          </svg>
          <PeerFace viewer={viewer} />
        </span>
      </span>
    </div>
  )
})

/** Someone's picture in a ring of their color: the head of their pointer and of their pins. */
function PeerFace({ viewer }: { viewer: LiveViewer }) {
  return (
    <span className={`avatar peer-face${viewer.kind === 'Agent' ? ' agent' : ''}`}>
      {viewer.kind === 'Agent' ? <SparkleIcon size={10} /> : <Face name={viewer.displayName} avatarUrl={viewer.avatarUrl} />}
    </span>
  )
}

/** The pins, plus those just gone for `ms` more (`out`, where they last were), so they can pull out. */
function useLeaving(pins: Pin[], ms: number): (Pin & { out?: boolean })[] {
  const [gone, setGone] = useState<Pin[]>([])
  /** The pins as last drawn, so those that go keep the place they last had. */
  const last = useRef(pins)
  const ids = pins.map((p) => p.id).join(' ')
  // Before the browser paints, so a pin that goes never disappears for a frame before it pulls out.
  useLayoutEffect(() => {
    const here = new Set(ids.split(' '))
    const left = last.current.filter((p) => !here.has(p.id))
    if (left.length === 0) return
    const leftIds = new Set(left.map((p) => p.id))
    const leaving = new Set(left)
    setGone((current) => [...current.filter((p) => !here.has(p.id) && !leftIds.has(p.id)), ...left])
    window.setTimeout(() => setGone((current) => current.filter((p) => !leaving.has(p))), ms)
  }, [ids, ms])
  useLayoutEffect(() => {
    last.current = pins
  })
  const here = new Set(ids.split(' '))
  return [...pins, ...gone.filter((p) => !here.has(p.id)).map((p) => ({ ...p, out: true }))]
}

/** The time, updated every few seconds while `on`, so idle heads fade without anything else changing. */
function useNow(on: boolean) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!on) return
    const timer = window.setInterval(() => setNow(Date.now()), 5000)
    return () => window.clearInterval(timer)
  }, [on])
  return now
}
