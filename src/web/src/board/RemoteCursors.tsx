import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties } from 'react'
import type { LiveViewer } from '../api/types'
import { Face } from '../components/Avatar'
import { presenceColor } from '../live/presence'
import type { CursorStore, LiveDrag } from '../live/useLive'
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
 * name. It follows the store by itself, so the board doesn't draw again as pointers move.
 */
export function RemoteCursors({ cursors, viewers, drags, placeOf, zoom, focused }: {
  cursors: CursorStore
  viewers: LiveViewer[]
  drags: LiveDrag[]
  placeOf: (id: string) => PinPlace | null
  zoom: number
  focused: string | null
}) {
  const peers = useSyncExternalStore(cursors.subscribe, cursors.snapshot)
  const dragging = new Set(drags.filter((d) => !d.endedAt).map((d) => d.key))
  const heads = headsOf(peers.cursors, viewers).filter((h) => !dragging.has(h.key))
  const pins = useLeaving(pinsOf(peers, viewers, placeOf, zoom), PIN_OUT_MS)
  const now = useNow(heads.length > 0)
  const headFocused = heads.some((h) => h.key === focused)
  const styleOf = (viewer: LiveViewer, at: { x: number; y: number }) =>
    ({ left: at.x, top: at.y, '--who': presenceColor(viewer.principalId), '--unzoom': 1 / zoom }) as CSSProperties
  return (
    <>
      {pins.map(({ id, key, viewer, at, out }) => (
        <div
          key={id}
          className={`peer-pin${out ? ' out' : ''}${!headFocused && focused === key ? ' focused' : ''}`}
          aria-hidden="true"
          style={styleOf(viewer, at)}
        >
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
      ))}
      {heads.map(({ key, viewer, at, updatedAt }) => (
        <div
          key={key}
          className={`peer-pointer${now - updatedAt > IDLE_MS ? ' idle' : ''}${focused === key ? ' focused' : ''}`}
          aria-hidden="true"
          style={styleOf(viewer, at)}
        >
          <PeerFace viewer={viewer} />
          {focused === key && <span className="peer-name">{viewer.displayName}</span>}
        </div>
      ))}
    </>
  )
}

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
    setGone((current) => [...current.filter((p) => !here.has(p.id) && !left.some((l) => l.id === p.id)), ...left])
    window.setTimeout(() => setGone((current) => current.filter((p) => !left.includes(p))), ms)
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
