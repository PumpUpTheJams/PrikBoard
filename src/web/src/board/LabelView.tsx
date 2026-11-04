import { useEffect, useLayoutEffect, useReducer, useRef, useState, type PointerEvent } from 'react'
import { createPortal } from 'react-dom'
import { labelSizes, type InkColor, type LabelDetail, type LabelSize, type Point, type Rect } from '../api/types'
import { inkColor } from './drawings'
import { keepPress } from './gesture'
import { LabelText } from './LabelText'
import { LinkIcon, ListBulletIcon, ListNumberIcon, TrashIcon } from './icons'
import { InkSwatches } from './DrawingView'
import { labelPixels, type LabelFormat, type LabelListKind } from './labels'

interface LabelViewProps {
  label: Pick<LabelDetail, 'id' | 'ink' | 'size' | 'content'>
  /** Where it is shown; its size comes from its text. */
  at: Point
  selected?: boolean
  lifted?: boolean
  /** Viewers follow its links by the browser's own click; for others a click selects, and the canvas decides. */
  readOnly?: boolean
  onPointerDown?: (e: PointerEvent<HTMLDivElement>) => void
}

/** A label on the board or on a card's canvas. */
export function LabelView({ label, at, selected, lifted, readOnly, onPointerDown }: LabelViewProps) {
  return (
    <div
      className={`label${selected ? ' selected' : ''}${lifted ? ' lifted' : ''}`}
      data-label={label.id}
      style={{ left: at.x, top: at.y, color: inkColor[label.ink], fontSize: labelPixels[label.size] }}
      onPointerDown={onPointerDown}
      onClick={readOnly ? undefined : (e) => e.preventDefault()}
    >
      <LabelText content={label.content} />
    </div>
  )
}

/** A label dragged out of the open card, at the board's zoom, held where it was grabbed. */
export function LabelGhost({ label, at, zoom }: { label: LabelDetail; at: Point; zoom: number }) {
  return createPortal(
    <div className="drag-ghost label-ghost" aria-hidden="true" style={{ left: at.x, top: at.y, transform: `scale(${zoom})` }}>
      <LabelView label={label} at={{ x: 0, y: 0 }} lifted />
    </div>,
    document.body,
  )
}

const FORMATS: { format: LabelFormat; letter: string; name: string; className: string }[] = [
  { format: 'bold', letter: 'B', name: 'Bold', className: 'label-bold' },
  { format: 'italic', letter: 'I', name: 'Italic', className: 'label-italic' },
  { format: 'underline', letter: 'U', name: 'Underline', className: 'label-underline' },
  { format: 'strike', letter: 'S', name: 'Strikethrough', className: 'label-strike' },
]

/** The sizes a label's text can have, as buttons. */
export function LabelSizes({ value, onChange }: { value: LabelSize | null; onChange: (size: LabelSize) => void }) {
  return labelSizes.map((size) => (
    <button key={size} type="button" className="label-tool" title={`Size ${size} (${labelPixels[size]} px)`} aria-label={`Size ${size}`} aria-pressed={size === value} onClick={() => onChange(size)}>
      {size}
    </button>
  ))
}

/** The flyout of the label tool: the ink and size new labels get. */
export function LabelOptions({ ink, onInkChange, size, onSizeChange }: { ink: InkColor; onInkChange: (ink: InkColor) => void; size: LabelSize; onSizeChange: (size: LabelSize) => void }) {
  return (
    <>
      <InkSwatches value={ink} onChange={onInkChange} />
      <span className="toolbar-divider" aria-hidden="true" />
      <LabelSizes value={size} onChange={onSizeChange} />
    </>
  )
}

export interface LabelLinkField {
  value: string
  error: boolean
  /** It edits a link that is there, so it can be removed. */
  existing: boolean
  onChange: (value: string) => void
  onApply: () => void
  onRemove: () => void
  onCancel: () => void
}

interface LabelToolbarProps {
  rect: Rect
  zoom: number
  isActive: (format: LabelFormat) => boolean
  onToggle: (format: LabelFormat) => void
  listActive: (kind: LabelListKind) => boolean
  onToggleList: (kind: LabelListKind) => void
  linkActive: boolean
  onLink: () => void
  onDelete: () => void
  link?: LabelLinkField | null
  /** Ink and size, only while the label is being edited. */
  style?: LabelToolbarStyle
}

interface LabelToolbarStyle {
  /** What the text selected has; null where it has several. */
  ink: InkColor | null
  size: LabelSize | null
  onInk: (ink: InkColor) => void
  onSize: (size: LabelSize) => void
}

/** Screen pixels kept between the toolbar and its label, and between it and the edge of the view. */
const TOOLBAR_GAP = 12
const TOOLBAR_MARGIN = 8
/** What floats over a canvas. Nothing on a zoomed canvas can be drawn above it, so the toolbar moves clear of it. */
const FLOATING = ':scope > .rail, :scope > .view-controls, :scope > .board-filters'

interface Box {
  left: number
  top: number
  right: number
  bottom: number
}

const overlaps = (a: Box, b: Box) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom
const moved = (box: Box, x: number, y: number): Box => ({ left: box.left + x, top: box.top + y, right: box.right + x, bottom: box.bottom + y })

/** How far a toolbar at `box` moves to clear what floats over the canvas: sideways if that stays in `view`, else up as far as there is room. */
function clearOfFloating(box: Box, floating: Box[], view: Box) {
  let x = 0
  let y = 0
  for (let pass = 0; pass <= 2 * floating.length; pass++) {
    const at = moved(box, x, y)
    const hit = floating.find((f) => overlaps(at, f))
    if (!hit) break
    const sideways = [hit.left - TOOLBAR_MARGIN - at.right, hit.right + TOOLBAR_MARGIN - at.left]
      .filter((dx) => {
        const to = moved(at, dx, 0)
        return to.left >= view.left + TOOLBAR_MARGIN && to.right <= view.right - TOOLBAR_MARGIN && !floating.some((f) => overlaps(to, f))
      })
      .sort((a, b) => Math.abs(a) - Math.abs(b))[0]
    if (sideways !== undefined) {
      x += sideways
      continue
    }
    const up = Math.max(hit.top - TOOLBAR_MARGIN - at.bottom, view.top + TOOLBAR_MARGIN - at.top)
    if (up >= 0) break
    y += up
  }
  return { x, y: Math.max(y, view.top + TOOLBAR_MARGIN - box.top) }
}

/** The toolbar above a selected label or one being edited, at the same size on screen at any zoom. */
export function LabelToolbar(props: LabelToolbarProps) {
  const { rect, zoom, isActive, onToggle, listActive, onToggleList, linkActive, onLink, onDelete, link, style } = props
  const toolbar = useRef<HTMLDivElement>(null)
  /** How far it slides to stay in view and clear of what floats over the canvas, in screen pixels, and whether it goes below the label for lack of room above. */
  const [place, setPlace] = useState({ x: 0, y: 0, below: 0 })
  const [, rendered] = useReducer((n: number) => n + 1, 0)
  // Scrolling doesn't render the canvas, but it changes what the toolbar has to keep clear of.
  useEffect(() => {
    const scroller = toolbar.current!.closest('.board-scroll, .card-scroll')
    scroller?.addEventListener('scroll', rendered, { passive: true })
    return () => scroller?.removeEventListener('scroll', rendered)
  }, [])
  useLayoutEffect(() => {
    const el = toolbar.current!
    const scroller = el.closest('.board-scroll, .card-scroll')
    if (!scroller) return
    const view = scroller.getBoundingClientRect()
    const box = el.getBoundingClientRect()
    const left = box.left - place.x
    const right = left + box.width
    // The label being edited grows, so its height comes from the page there.
    const height = rect.height || el.parentElement!.offsetHeight
    const above = box.top - place.y - place.below
    const below = above < view.top + TOOLBAR_MARGIN ? height * zoom + box.height + 2 * TOOLBAR_GAP : 0
    const top = above + below
    const edge = view.left + scroller.clientWidth - TOOLBAR_MARGIN
    const inView = left < view.left + TOOLBAR_MARGIN ? view.left + TOOLBAR_MARGIN - left : right > edge ? Math.max(view.left + TOOLBAR_MARGIN - left, edge - right) : 0
    const floating = [...scroller.parentElement!.querySelectorAll(FLOATING)].map((f) => f.getBoundingClientRect())
    const clear = clearOfFloating({ left: left + inView, top, right: right + inView, bottom: top + box.height }, floating, { left: view.left, top: view.top, right: edge + TOOLBAR_MARGIN, bottom: view.bottom })
    // Whole pixels, or rounding in the scale could keep it from ever settling.
    const x = Math.round(inView + clear.x)
    const y = Math.round(clear.y)
    if (x !== place.x || y !== place.y || below !== place.below) setPlace({ x, y, below })
  })
  return (
    <div
      ref={toolbar}
      className={`drawing-toolbar label-toolbar${link ? ' with-link' : ''}`}
      role="toolbar"
      aria-label="Label"
      style={{ left: rect.x, top: rect.y, transform: `scale(${1 / zoom}) translate(${place.x}px, calc(-100% - ${TOOLBAR_GAP}px + ${place.below + place.y}px))` }}
      // A press here keeps the text selected in the label being edited, except in the link's own field.
      onPointerDown={keepPress}
      onMouseDown={(e) => {
        if (!(e.target instanceof HTMLInputElement)) e.preventDefault()
      }}
    >
      {FORMATS.map(({ format, letter, name, className }) => (
        <button key={format} type="button" className={`label-tool ${className}`} title={name} aria-label={name} aria-pressed={isActive(format)} onClick={() => onToggle(format)}>
          {letter}
        </button>
      ))}
      <span className="toolbar-divider" aria-hidden="true" />
      <button type="button" className="label-tool" title="Bulleted list" aria-label="Bulleted list" aria-pressed={listActive('Bullet')} onClick={() => onToggleList('Bullet')}>
        <ListBulletIcon />
      </button>
      <button type="button" className="label-tool" title="Numbered list" aria-label="Numbered list" aria-pressed={listActive('Numbered')} onClick={() => onToggleList('Numbered')}>
        <ListNumberIcon />
      </button>
      <button type="button" className="label-tool" title="Link" aria-label="Link" aria-pressed={linkActive} onClick={onLink}>
        <LinkIcon />
      </button>
      <span className="toolbar-divider" aria-hidden="true" />
      {style && (
        <>
          <InkSwatches value={style.ink} onChange={style.onInk} />
          <span className="toolbar-divider" aria-hidden="true" />
          <LabelSizes value={style.size} onChange={style.onSize} />
          <span className="toolbar-divider" aria-hidden="true" />
        </>
      )}
      <button type="button" className="drawing-delete" aria-label="Delete label" title="Delete label (Del)" onClick={onDelete}>
        <TrashIcon size={15} />
      </button>
      {link && (
        <div className="label-link-field">
          <input
            type="text"
            autoFocus
            aria-label="Link address"
            placeholder="https://example.com or a card link"
            value={link.value}
            onChange={(e) => link.onChange(e.target.value)}
            onKeyDown={(e) => {
              e.stopPropagation()
              if (e.key === 'Enter') {
                e.preventDefault()
                link.onApply()
              } else if (e.key === 'Escape') {
                e.preventDefault()
                link.onCancel()
              }
            }}
          />
          <button type="button" onClick={link.onApply}>Apply</button>
          {link.existing && <button type="button" onClick={link.onRemove}>Remove</button>}
          {link.error && <span className="label-link-error">Enter an http(s) link or a card link.</span>}
        </div>
      )}
    </div>
  )
}
