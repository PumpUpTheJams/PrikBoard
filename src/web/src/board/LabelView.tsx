import { useEffect, useLayoutEffect, useReducer, useRef, useState, type PointerEvent } from 'react'
import { createPortal } from 'react-dom'
import { labelSizes, type InkColor, type LabelDetail, type LabelSize, type Point, type Rect, type ResizeHandle } from '../api/types'
import { inkColor } from './drawings'
import { keepPress } from './gesture'
import { LabelText } from './LabelText'
import { LinkIcon, ListBulletIcon, ListNumberIcon, TrashIcon } from './icons'
import { InkSwatches } from './DrawingView'
import { labelPixels, MIN_LABEL_BOX, type LabelBlockTool, type LabelFormat, type LabelListKind } from './labels'
import { ResizeHandles } from './ResizeHandles'

interface LabelViewProps {
  label: Pick<LabelDetail, 'id' | 'ink' | 'size' | 'content' | 'wrapWidth' | 'minHeight' | 'background'>
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
      style={{ left: at?.x, top: at?.y, width: label.wrapWidth ?? undefined, minHeight: label.minHeight ?? undefined, color: inkColor[label.ink], fontSize: labelPixels[label.size] }}
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
  { kind: 'Quote', letter: '❝', name: 'Quote' },
  { kind: 'Code', letter: '{ }', name: 'Code block' },
]

/** The sizes a label's text can have, as buttons, named as font sizes: S on its own could be strikethrough. */
export function LabelSizes({ value, onChange }: { value: LabelSize | null; onChange: (size: LabelSize) => void }) {
  return (
    <div className="label-sizes" role="group" aria-label="Font size">
      <span className="label-sizes-caption" aria-hidden="true">
        Font size
      </span>
      {labelSizes.map((size) => (
        <button key={size} type="button" className="label-tool" title={`Font size ${size} (${labelPixels[size]} px)`} aria-label={`Font size ${size}`} aria-pressed={size === value} onClick={() => onChange(size)}>
          {size}
        </button>
      ))}
    </div>
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
  /** Ink and size. */
  style: LabelToolbarStyle
  /** Its background, on or off. */
  background: { on: boolean; onToggle: () => void }
  /** Turning text into other blocks. */
  blocks: { isActive: (kind: LabelBlockTool) => boolean; onToggle: (kind: LabelBlockTool) => void }
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

/** The ink of the text being edited: a button showing it, whose palette opens in a popup until an ink is picked or something else is pressed. */
function LabelColor({ ink, onChange }: { ink: InkColor | null; onChange: (ink: InkColor) => void }) {
  const [open, setOpen] = useState(false)
  const [upwards, setUpwards] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const popup = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const press = (e: globalThis.PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', press, true)
    return () => document.removeEventListener('pointerdown', press, true)
  }, [open])

  // Below the button, unless that is out of view or under what floats over the canvas: nothing on a zoomed canvas can be drawn above it.
  useLayoutEffect(() => {
    if (!open) return
    const scroller = root.current!.closest('.board-scroll, .card-scroll')
    if (!scroller) return
    const button = root.current!.getBoundingClientRect()
    const { width, height } = popup.current!.getBoundingClientRect()
    const below = { left: button.left, right: button.left + width, top: button.bottom + POPUP_GAP, bottom: button.bottom + POPUP_GAP + height }
    const view = scroller.getBoundingClientRect()
    const floating = [...scroller.parentElement!.querySelectorAll(FLOATING)].map((f) => f.getBoundingClientRect())
    const room = view.top + height + POPUP_GAP <= button.top
    setUpwards(room && (below.bottom > view.bottom || floating.some((f) => overlaps(below, f))))
  }, [open])

  return (
    <div className="label-color" ref={root}>
      <button type="button" className="label-tool label-text-tool" aria-haspopup="true" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span className={`label-color-dot${ink ? '' : ' mixed'}`} style={ink ? { background: inkColor[ink] } : undefined} aria-hidden="true" />
        Color
      </button>
      {open && (
        <div ref={popup} className={`label-color-popup${upwards ? ' up' : ''}`}>
          <InkSwatches
            value={ink}
            onChange={(picked) => {
              onChange(picked)
              setOpen(false)
            }}
          />
        </div>
      )}
    </div>
  )
}

/** The buttons of a label's toolbar, on a canvas or on the mobile layout's own screen. */
export function LabelTools({ isActive, onToggle, listActive, onToggleList, linkActive, onLink, onDelete, link, style, blocks, background }: LabelToolsProps) {
  return (
    <>
      <div className="label-tool-row" role="group" aria-label="Text">
        <div className="label-tool-group">
          {FORMATS.map(({ format, letter, name, className }) => (
            <button key={format} type="button" className={`label-tool ${className}`} title={name} aria-label={name} aria-pressed={isActive(format)} onClick={() => onToggle(format)}>
              {letter}
            </button>
          ))}
          <button type="button" className="label-tool" title="Link" aria-label="Link" aria-pressed={linkActive} onClick={onLink}>
            <LinkIcon />
          </button>
        </div>
        <span className="toolbar-divider" aria-hidden="true" />
        <div className="label-tool-group">
          <LabelColor ink={style.ink} onChange={style.onInk} />
          <span className="toolbar-divider" aria-hidden="true" />
          <LabelSizes value={style.size} onChange={style.onSize} />
        </div>
      </div>
      <div className="label-tool-row" role="group" aria-label="Blocks">
        <div className="label-tool-group">
          {BLOCKS.map(({ kind, letter, name }) => (
            <button key={kind} type="button" className="label-tool" title={name} aria-label={name} aria-pressed={blocks.isActive(kind)} onClick={() => blocks.onToggle(kind)}>
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
          <button type="button" className="label-tool" title="Task list" aria-label="Task list" aria-pressed={blocks.isActive('Task')} onClick={() => blocks.onToggle('Task')}>
            ☑
          </button>
          <span className="toolbar-divider" aria-hidden="true" />
          <button type="button" className="label-tool" title="Table" aria-label="Table" aria-pressed={blocks.isActive('Table')} onClick={() => blocks.onToggle('Table')}>
            ▦
          </button>
        </div>
        <span className="toolbar-divider" aria-hidden="true" />
        <div className="label-tool-group label-tool-end">
          <button type="button" className="label-tool label-text-tool" aria-pressed={background.on} onClick={background.onToggle}>
            Background
          </button>
          <button type="button" className="drawing-delete" aria-label="Delete label" title="Delete label (Del)" onClick={onDelete}>
            <TrashIcon size={15} />
          </button>
        </div>
      </div>
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

/** Screen pixels between the Color button and its palette. */
const POPUP_GAP = 6

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

/** A label's box: its corner, the width its text wraps at, and the height it keeps with little text. */
export interface LabelBox {
  x: number
  y: number
  width: number
  minHeight: number | null
}

const LABEL_GRID = 20
const MIN_BOX_HEIGHT = 20
const onGrid = (value: number) => Math.round(value / LABEL_GRID) * LABEL_GRID

interface LabelBoxHandlesProps {
  /** The label selected, by its id; without one, the label being edited, in the element around these handles. */
  id?: string
  at: Point
  minHeight: number | null
  zoom: number
  onResize: (box: LabelBox) => void
  /** A double-click on a handle: back to as wide and as tall as its text. */
  onReset?: () => void
}

/**
 * The handles of a label's box, on its corners and edges like those of an image, but free in both directions: its text wraps
 * in the box, and it grows past the box when the text doesn't fit. The label follows on the page until `onResize` is done with it.
 */
export function LabelBoxHandles({ id, at, minHeight, zoom, onResize, onReset }: LabelBoxHandlesProps) {
  const root = useRef<HTMLDivElement>(null)
  const labelEl = () => root.current?.parentElement?.querySelector<HTMLElement>(id ? `[data-label="${id}"]` : '.label.editing') ?? null

  // As big as the label is on the page, which its text decides as much as its box.
  useLayoutEffect(() => {
    const el = labelEl()
    if (!el) return
    const fit = () => {
      root.current!.style.width = `${el.offsetWidth}px`
      root.current!.style.height = `${el.offsetHeight}px`
    }
    fit()
    const observer = new ResizeObserver(fit)
    observer.observe(el)
    return () => observer.disconnect()
  }, [id])

  function press(e: PointerEvent<HTMLElement>, handle: ResizeHandle) {
    keepPress(e)
    const el = labelEl()
    if (e.button !== 0 || !el) return
    const grip = e.currentTarget
    grip.setPointerCapture(e.pointerId)
    const start = { clientX: e.clientX, clientY: e.clientY, width: el.offsetWidth, height: el.offsetHeight }
    const positioned = id ? el : (root.current!.parentElement as HTMLElement)
    let box: LabelBox | null = null

    const move = (m: globalThis.PointerEvent) => {
      const dx = (m.clientX - start.clientX) / zoom
      const dy = (m.clientY - start.clientY) / zoom
      const [right, bottom] = [at.x + start.width, at.y + start.height]
      let [x, y, width, height] = [at.x, at.y, start.width, start.height]
      if (handle.includes('Right')) width = Math.max(MIN_LABEL_BOX, onGrid(right + dx) - at.x)
      if (handle.includes('Left')) {
        x = Math.min(onGrid(at.x + dx), right - MIN_LABEL_BOX)
        width = right - x
      }
      if (handle.includes('Bottom')) height = Math.max(MIN_BOX_HEIGHT, onGrid(bottom + dy) - at.y)
      if (handle.includes('Top')) {
        y = Math.min(onGrid(at.y + dy), bottom - MIN_BOX_HEIGHT)
        height = bottom - y
      }
      const sized = handle.includes('Top') || handle.includes('Bottom')
      box = { x, y, width, minHeight: sized ? height : minHeight }
      positioned.style.left = `${x}px`
      positioned.style.top = `${y}px`
      el.classList.add('wraps')
      el.style.width = `${width}px`
      if (sized) el.style.minHeight = `${height}px`
      if (id) {
        root.current!.style.left = `${x}px`
        root.current!.style.top = `${y}px`
      }
    }
    const end = () => {
      grip.removeEventListener('pointermove', move)
      grip.removeEventListener('pointerup', end)
      grip.removeEventListener('pointercancel', end)
      if (box) onResize(box)
    }
    grip.addEventListener('pointermove', move)
    grip.addEventListener('pointerup', end)
    grip.addEventListener('pointercancel', end)
  }

  return (
    <div
      ref={root}
      className="label-box"
      style={id ? { left: at.x, top: at.y } : { left: 0, top: 0 }}
      // Keeps the text selected in the label being edited.
      onMouseDown={(e) => e.preventDefault()}
      onDoubleClick={(e) => {
        e.stopPropagation()
        onReset?.()
      }}
    >
      <ResizeHandles onPointerDown={press} />
    </div>
  )
}
