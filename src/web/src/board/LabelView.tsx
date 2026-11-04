import { useEffect, useLayoutEffect, useReducer, useRef, useState, type PointerEvent } from 'react'
import { createPortal } from 'react-dom'
import { labelInks, labelSizes, type InkColor, type LabelDetail, type LabelSize, type Point, type Rect } from '../api/types'
import { inkColor } from './drawings'
import { keepPress } from './gesture'
import { LabelText } from './LabelText'
import { LinkIcon, ListBulletIcon, ListNumberIcon, TrashIcon } from './icons'
import { InkSwatches } from './DrawingView'
import { labelPixels, type LabelBlockTool, type LabelFormat, type LabelListKind } from './labels'

interface LabelViewProps {
  label: Pick<LabelDetail, 'id' | 'ink' | 'size' | 'content' | 'wrapWidth' | 'background'>
  /** Where it is shown; its size comes from its text. Without a place it takes its turn in a list, as in the mobile layout. */
  at?: Point
  selected?: boolean
  lifted?: boolean
  /** A search found something in it. */
  matched?: boolean
  /** Viewers follow its links by the browser's own click; for others a click selects, and the canvas decides. */
  readOnly?: boolean
  onPointerDown?: (e: PointerEvent<HTMLDivElement>) => void
  /** A mouse came over it or left it (a touch has nothing to hover with). */
  onHover?: (over: boolean) => void
}

/** A label on the board or on a card's canvas. */
export function LabelView({ label, at, selected, lifted, matched, readOnly, onPointerDown, onHover }: LabelViewProps) {
  return (
    <div
      className={`label${selected ? ' selected' : ''}${lifted ? ' lifted' : ''}${matched ? ' matched' : ''}${label.wrapWidth ? ' wraps' : ''}${label.background ? ' with-background' : ''}`}
      data-label={label.id}
      style={{ left: at?.x, top: at?.y, width: label.wrapWidth ?? undefined, color: inkColor[label.ink], fontSize: labelPixels[label.size] }}
      onPointerDown={onPointerDown}
      onPointerEnter={onHover && ((e) => e.pointerType !== 'touch' && onHover(true))}
      onPointerLeave={onHover && ((e) => e.pointerType !== 'touch' && onHover(false))}
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
  { format: 'code', letter: '</>', name: 'Code', className: 'label-code' },
]

const BLOCKS: { kind: LabelBlockTool; letter: string; name: string }[] = [
  { kind: 'Heading', letter: 'H', name: 'Heading' },
  { kind: 'Task', letter: '☑', name: 'Task list' },
  { kind: 'Quote', letter: '❝', name: 'Quote' },
  { kind: 'Code', letter: '{ }', name: 'Code block' },
  { kind: 'Table', letter: '▦', name: 'Table' },
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
      <InkSwatches value={ink} onChange={onInkChange} inks={labelInks} />
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

export interface LabelToolsProps {
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
  /** Its background, on or off. */
  background: { on: boolean; onToggle: () => void }
  /** Turning text into other blocks, only while the label is being edited. */
  blocks?: { isActive: (kind: LabelBlockTool) => boolean; onToggle: (kind: LabelBlockTool) => void }
}

interface LabelToolbarProps extends LabelToolsProps {
  rect: Rect
  zoom: number
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
  const { rect, zoom, ...tools } = props
  const { link } = tools
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
      <LabelTools {...tools} />
    </div>
  )
}

/** The buttons of a label's toolbar, on a canvas or on the mobile layout's own screen. */
export function LabelTools({ isActive, onToggle, listActive, onToggleList, linkActive, onLink, onDelete, link, style, blocks, background }: LabelToolsProps) {
  return (
    <>
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
      <button type="button" className="label-tool label-text-tool" aria-pressed={background.on} onClick={background.onToggle}>
        Background
      </button>
      {blocks && BLOCKS.map(({ kind, letter, name }) => (
        <button key={kind} type="button" className="label-tool" title={name} aria-label={name} aria-pressed={blocks.isActive(kind)} onClick={() => blocks.onToggle(kind)}>
          {letter}
        </button>
      ))}
      <span className="toolbar-divider" aria-hidden="true" />
      {style && (
        <>
          <InkSwatches value={style.ink} onChange={style.onInk} inks={labelInks} />
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
    </>
  )
}

/** Screen pixels between the bar of a label and the label's corner. */
const BAR_INSET = 8

interface LabelBarProps {
  rect: Rect
  zoom: number
  background: { on: boolean; onToggle: () => void }
  onEdit: () => void
  onDelete: () => void
  onPointerEnter?: () => void
  onPointerLeave?: () => void
}

/**
 * The bar of a selected or hovered label, in its top-right corner like the delete button of an image, at the same size on screen at any
 * zoom: styling is for editing. It goes above a label too small to hold it, whose text it would cover.
 */
export function LabelBar({ rect, zoom, background, onEdit, onDelete, onPointerEnter, onPointerLeave }: LabelBarProps) {
  const bar = useRef<HTMLDivElement>(null)
  const [place, setPlace] = useState({ x: 0, y: 0, above: false })
  const [, rendered] = useReducer((n: number) => n + 1, 0)
  // Scrolling doesn't render the canvas, but it changes what the bar has to keep clear of.
  useEffect(() => {
    const scroller = bar.current!.closest('.board-scroll, .card-scroll')
    scroller?.addEventListener('scroll', rendered, { passive: true })
    return () => scroller?.removeEventListener('scroll', rendered)
  }, [])
  useLayoutEffect(() => {
    const el = bar.current!
    const scroller = el.closest('.board-scroll, .card-scroll')
    if (!scroller) return
    const box = el.getBoundingClientRect()
    const above = rect.height * zoom < box.height + 2 * BAR_INSET || rect.width * zoom < box.width + 2 * BAR_INSET
    // Where it would be with nothing moving it.
    const left = box.left - place.x
    const top = box.top - place.y + (above === place.above ? 0 : above ? -box.height - 2 * BAR_INSET : box.height + 2 * BAR_INSET)
    const view = scroller.getBoundingClientRect()
    const floating = [...scroller.parentElement!.querySelectorAll(FLOATING)].map((f) => f.getBoundingClientRect())
    const clear = clearOfFloating({ left, top, right: left + box.width, bottom: top + box.height }, floating, { left: view.left, top: view.top, right: view.left + scroller.clientWidth, bottom: view.bottom })
    const x = Math.round(clear.x)
    const y = Math.round(clear.y)
    if (x !== place.x || y !== place.y || above !== place.above) setPlace({ x, y, above })
  })
  return (
    <div
      ref={bar}
      className="drawing-toolbar label-bar"
      role="toolbar"
      aria-label="Label"
      style={{
        left: rect.x + rect.width,
        top: rect.y,
        transform: `scale(${1 / zoom}) translate(calc(-100% - ${BAR_INSET}px + ${place.x}px), calc(${place.above ? '-100% - ' : ''}${BAR_INSET}px + ${place.y}px))`,
      }}
      onPointerDown={keepPress}
      onPointerEnter={onPointerEnter}
      onPointerLeave={onPointerLeave}
    >
      <button type="button" className="label-tool label-text-tool" aria-pressed={background.on} onClick={background.onToggle}>
        Background
      </button>
      <button type="button" className="label-tool label-text-tool" onClick={onEdit}>
        Edit
      </button>
      <button type="button" className="drawing-delete" aria-label="Delete label" title="Delete label (Del)" onClick={onDelete}>
        <TrashIcon size={15} />
      </button>
    </div>
  )
}

/** Screen pixels of the handle that sets where a selected label's text wraps. */
const WRAP_HANDLE = 10

/**
 * The handle on the right edge of a selected label: dragged, the text wraps at that width (shown on the label itself until
 * the reload brings it), and double-clicked it stops wrapping.
 */
export function LabelWrapHandle({ id, rect, zoom, onWrap }: { id: string; rect: Rect; zoom: number; onWrap: (width: number) => void }) {
  const handle = useRef<HTMLDivElement>(null)
  const drag = useRef<{ startX: number; width: number; moved: boolean } | null>(null)
  const labelEl = () => handle.current?.parentElement?.querySelector<HTMLElement>(`[data-label="${id}"]`) ?? null
  // As tall as the label is on the page: what an agent wrote only has an estimate stored.
  useLayoutEffect(() => {
    const el = labelEl()
    if (el && handle.current) handle.current.style.height = `${el.offsetHeight}px`
  })
  return (
    <div
      ref={handle}
      className="label-wrap-handle"
      title="Drag to wrap the text; double-click to stop wrapping"
      style={{ left: rect.x + rect.width, top: rect.y, height: rect.height, width: WRAP_HANDLE / zoom }}
      onPointerDown={(e) => {
        keepPress(e)
        if (e.button !== 0) return
        e.currentTarget.setPointerCapture(e.pointerId)
        drag.current = { startX: e.clientX, width: rect.width, moved: false }
      }}
      onPointerMove={(e) => {
        const d = drag.current
        if (!d) return
        const width = Math.max(MIN_WRAP_WIDTH, Math.round(rect.width + (e.clientX - d.startX) / zoom))
        if (width === d.width) return
        drag.current = { ...d, width, moved: true }
        const el = labelEl()
        if (el) {
          el.classList.add('wraps')
          el.style.width = `${width}px`
        }
        e.currentTarget.style.left = `${rect.x + width}px`
        e.currentTarget.style.height = `${el?.offsetHeight ?? rect.height}px`
      }}
      onPointerUp={() => {
        const d = drag.current
        drag.current = null
        if (d?.moved) onWrap(d.width)
      }}
      onDoubleClick={(e) => {
        e.stopPropagation()
        onWrap(0)
      }}
    />
  )
}

/** BoardLabel.MinWrapWidth on the server. */
const MIN_WRAP_WIDTH = 60
