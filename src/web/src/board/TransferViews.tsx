import { useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import type { Point } from '../api/types'
import { keepPress } from './gesture'
import { snapPoint } from './layout'
import { progressText, sizeText, type ClipShape, type TransferProgress } from './transfer'

/** How far along it is, from 0 to 1; null when that isn't known (the bar then moves on its own). */
function fraction(progress: TransferProgress | null) {
  if (!progress || progress.step === 'checking' || progress.step === 'saving') return null
  if (progress.step === 'uploading' && progress.bytesTotal) return progress.bytes / progress.bytesTotal
  return progress.imagesTotal > 0 ? progress.images / progress.imagesTotal : null
}

function ProgressBar({ progress }: { progress: TransferProgress | null }) {
  const done = fraction(progress)
  return (
    <div className={`transfer-bar${done === null ? ' waiting' : ''}`} role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={done === null ? undefined : Math.round(done * 100)}>
      <span style={done === null ? undefined : { width: `${Math.min(100, done * 100)}%` }} />
    </div>
  )
}

interface CopyPanelProps {
  text: string
  progress: TransferProgress | null
  /** Loaded but not on the clipboard yet: a press of Ctrl/Cmd+C or of Copy puts it there. */
  ready: boolean
  onCopy: () => void
  onCancel: () => void
}

/** At the bottom of the board while a copy with images comes in, or waits to be put on the clipboard. */
export function CopyPanel({ text, progress, ready, onCopy, onCancel }: CopyPanelProps) {
  const keys = navigator.platform.startsWith('Mac') ? '⌘C' : 'Ctrl+C'
  return (
    <div className="transfer-panel" role="status" aria-live="polite">
      <div className="transfer-text">{ready ? `Ready to copy: press ${keys} again` : text}</div>
      {!ready && <ProgressBar progress={progress} />}
      {ready && (
        <button type="button" className="transfer-button primary" onClick={onCopy}>
          Copy
        </button>
      )}
      <button type="button" className="transfer-button" onClick={onCancel}>
        Cancel
      </button>
    </div>
  )
}

interface PasteGhostProps {
  shape: ClipShape
  /** Where its top-left is placed; null while it follows the pointer, until a click places it. */
  at: Point | null
  /** Where the pointer was last, to start from. */
  start: Point | null
  zoom: number
  /** What is pasted ("7 buckets, 1,250 cards and 100 images") and how big it is. */
  summary: string
  size: number
  progress: TransferProgress | null
  scrollRef: RefObject<HTMLDivElement | null>
  toCanvas: (e: { clientX: number; clientY: number }) => Point
  onPlace: (at: Point) => void
  onCancel: () => void
}

/**
 * The outline of a big paste on the board: it follows the pointer until a click places it, and then stays where the
 * paste lands with its progress and a Cancel button until the board has it.
 */
export function PasteGhost({ shape, at, start, zoom, summary, size, progress, scrollRef, toCanvas, onPlace, onCancel }: PasteGhostProps) {
  const [pointer, setPointer] = useState<Point | null>(start && snapPoint(start))
  const latest = useRef({ toCanvas, onPlace })
  useEffect(() => {
    latest.current = { toCanvas, onPlace }
  })
  const placing = at === null

  // While it is placed, the pointer moves it and a click on the board puts it down (before the board sees the click).
  useEffect(() => {
    if (!placing) return
    const move = (e: PointerEvent) => setPointer(snapPoint(latest.current.toCanvas(e)))
    const down = (e: PointerEvent) => {
      if (e.button !== 0 || !(e.target instanceof Node) || !scrollRef.current?.contains(e.target)) return
      if (e.target instanceof Element && e.target.closest('.paste-ghost-label')) return
      e.preventDefault()
      e.stopPropagation()
      latest.current.onPlace(snapPoint(latest.current.toCanvas(e)))
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerdown', down, { capture: true })
    return () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerdown', down, { capture: true })
    }
  }, [placing, scrollRef])

  const paths = useMemo(() => {
    const outline = (rects: ClipShape['cards']) => rects.map((r) => `M${r.x} ${r.y}h${r.width}v${r.height}h${-r.width}Z`).join('')
    return { cards: outline(shape.cards), others: outline(shape.others) }
  }, [shape])

  const spot = at ?? pointer
  if (!spot) return null
  const label = placing ? `${summary} · ${sizeText(size)}` : progressText(progress, summary)
  return (
    <>
      <div className={`paste-ghost${placing ? '' : ' placed'}`} aria-hidden="true" style={{ left: spot.x, top: spot.y, width: shape.width, height: shape.height }}>
        <svg width={shape.width} height={shape.height} viewBox={`0 0 ${shape.width} ${shape.height}`}>
          {shape.buckets.map((b, i) => (
            <rect key={i} className="paste-ghost-bucket" x={b.x} y={b.y} width={b.width} height={b.height} rx={12} vectorEffect="non-scaling-stroke" />
          ))}
          <path className="paste-ghost-cards" d={paths.cards} vectorEffect="non-scaling-stroke" />
          <path className="paste-ghost-others" d={paths.others} vectorEffect="non-scaling-stroke" />
        </svg>
      </div>
      <div
        className="paste-ghost-label"
        role="status"
        aria-live="polite"
        style={{ left: spot.x, top: spot.y - 12 / zoom, transform: `scale(${1 / zoom}) translateY(-100%)` }}
        onPointerDown={keepPress}
      >
        <div className="transfer-text">{label}</div>
        {/* While it is placed the label moves with the pointer, so a button on it could never be reached: Esc cancels. */}
        {placing ? (
          <div className="transfer-hint">Click to place it · Esc to cancel</div>
        ) : (
          <>
            <ProgressBar progress={progress} />
            <button type="button" className="transfer-button" disabled={progress?.step === 'saving'} onClick={onCancel}>
              {progress?.step === 'saving' ? 'Finishing…' : 'Cancel'}
            </button>
          </>
        )}
      </div>
    </>
  )
}
