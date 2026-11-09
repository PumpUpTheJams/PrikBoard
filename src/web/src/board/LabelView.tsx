import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type PointerEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { labelSizes, labelText, type InkColor, type LabelDetail, type LabelSize, type Point, type Rect, type ResizeHandle } from '../api/types'
import { AboveItem } from './AboveItem'
import { floatingOver, overlaps, scrollerOf } from './placeOnCanvas'
import { inkColor } from './drawings'
import { keepPress, keepTextSelected } from './gesture'
import { LabelText } from './LabelText'
import { CloseIcon, LabelIcon, LinkIcon, ListBulletIcon, ListNumberIcon, TrashIcon } from './icons'
import { InkSwatches } from './DrawingView'
import { snap } from './layout'
import { farBars, labelClasses, labelPixels, MAX_LABEL_TITLE, MIN_LABEL_BOX, type LabelBlockTool, type LabelFormat, type LabelListKind } from './labels'
import { ResizeHandles } from './ResizeHandles'
import type { LabelHover } from './useLabelHover'

/** A label's caption: its kind's icon and the title, as a component's header shows them. */
export function LabelHead({ title }: { title: string }) {
  return (
    <header className="label-head">
      <LabelIcon size={14} />
      <span className="label-title">{title}</span>
    </header>
  )
}

interface LabelViewProps {
  label: Pick<LabelDetail, 'id' | 'ink' | 'size' | 'content' | 'wrapWidth' | 'minHeight' | 'background' | 'title'>
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
      className={`label${selected ? ' selected' : ''}${lifted ? ' lifted' : ''}${matched ? ' matched' : ''}${labelClasses(label)}`}
      data-label={label.id}
      style={{ left: at?.x, top: at?.y, width: label.wrapWidth ?? undefined, minHeight: label.minHeight ?? undefined, color: inkColor[label.ink], fontSize: labelPixels[label.size] }}
      onPointerDown={onPointerDown}
      onPointerEnter={onHover && ((e) => e.pointerType !== 'touch' && onHover(true))}
      onPointerLeave={onHover && ((e) => e.pointerType !== 'touch' && onHover(false))}
      onClick={readOnly ? undefined : (e) => e.preventDefault()}
    >
      {label.title && <LabelHead title={label.title} />}
      <LabelText content={label.content} />
    </div>
  )
}

interface FarLabelProps {
  label: LabelDetail
  at: Point
  selected: boolean
  lifted: boolean
  onPointerDown: (e: PointerEvent<HTMLDivElement>) => void
  onHover: (over: boolean) => void
}

/**
 * A label when the board is zoomed so far out that its text can't be read: its size and fill, with bars where its lines are.
 * It is one element with the bars as its background, where its text would be ten or more, so a board of labels stays light.
 */
export function FarLabel({ label, at, selected, lifted, onPointerDown, onHover }: FarLabelProps) {
  return (
    <div
      className={`label far${selected ? ' selected' : ''}${lifted ? ' lifted' : ''}${labelClasses(label)}`}
      data-label={label.id}
      role="img"
      aria-label={labelText(label)}
      style={{
        left: at.x,
        top: at.y,
        width: label.width,
        height: label.height,
        color: inkColor[label.ink],
        ...farBackground(label),
      }}
      onPointerDown={onPointerDown}
      onPointerEnter={(e) => e.pointerType !== 'touch' && onHover(true)}
      onPointerLeave={(e) => e.pointerType !== 'touch' && onHover(false)}
    />
  )
}

const FAR_BAR = 'linear-gradient(color-mix(in srgb, currentColor 70%, transparent) 0 0)'

/** Worked out once per version of a label, which a reload keeps when nothing about it changed: a zoom past the far zoom shows them all at once. */
const farBackgrounds = new WeakMap<LabelDetail, { backgroundImage: string; backgroundSize: string; backgroundPosition: string }>()

function farBackground(label: LabelDetail) {
  let background = farBackgrounds.get(label)
  if (!background) {
    const bars = farBars(label)
    background = {
      backgroundImage: bars.map(() => FAR_BAR).join(','),
      backgroundSize: bars.map((b) => `${Math.round(b.width)}px ${Math.round(b.height)}px`).join(','),
      backgroundPosition: bars.map((b) => `${Math.round(b.x)}px ${Math.round(b.y)}px`).join(','),
    }
    farBackgrounds.set(label, background)
  }
  return background
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

const FORMATS: { format: LabelFormat; letter: string; name: string; className?: string }[] = [
  { format: 'bold', letter: 'B', name: 'Bold', className: 'label-bold' },
  { format: 'italic', letter: 'I', name: 'Italic', className: 'label-italic' },
  { format: 'underline', letter: 'U', name: 'Underline', className: 'label-underline' },
  { format: 'strike', letter: 'S', name: 'Strikethrough', className: 'label-strike' },
  { format: 'code', letter: '</>', name: 'Code' },
]

const BLOCKS: { kind: LabelBlockTool; letter: string; name: string }[] = [
  { kind: 'Heading', letter: 'H', name: 'Heading' },
  { kind: 'Quote', letter: '❝', name: 'Quote' },
  { kind: 'Code', letter: '{ }', name: 'Code block' },
]

/** The sizes a label's text can have, as buttons, named as font sizes: S on its own could be strikethrough. */
function LabelSizes({ value, onChange }: { value: LabelSize | null; onChange: (size: LabelSize) => void }) {
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
  /** Its caption, typed in the toolbar. */
  caption: { value: string; onChange: (value: string) => void; onDone: () => void }
  /** Turning text into other blocks. */
  blocks: { isActive: (kind: LabelBlockTool) => boolean; onToggle: (kind: LabelBlockTool) => void }
}

interface LabelToolbarStyle {
  /** What the text selected has; null where it has several. */
  ink: InkColor | null
  size: LabelSize | null
  onInk: (ink: InkColor) => void
  onSize: (size: LabelSize) => void
}

/** The toolbar above a label being edited, at the same size on screen at any zoom. */
export function LabelToolbar({ zoom, ...tools }: LabelToolsProps & { zoom: number }) {
  return (
    <AboveItem
      className="drawing-toolbar label-toolbar"
      role="toolbar"
      label="Label"
      // The label being edited grows, so its height comes from the page.
      height={(el) => el.parentElement!.offsetHeight}
      zoom={zoom}
      // A press here keeps the text selected in the label being edited, except in the toolbar's own fields.
      onMouseDown={keepTextSelected}
    >
      <LabelTools {...tools} />
    </AboveItem>
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
    const scroller = scrollerOf(root.current!)
    if (!scroller) return
    const button = root.current!.getBoundingClientRect()
    const { width, height } = popup.current!.getBoundingClientRect()
    const below = { left: button.left, right: button.left + width, top: button.bottom + POPUP_GAP, bottom: button.bottom + POPUP_GAP + height }
    const view = scroller.getBoundingClientRect()
    const room = view.top + height + POPUP_GAP <= button.top
    setUpwards(room && (below.bottom > view.bottom || floatingOver(scroller).some((f) => overlaps(below, f))))
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

function ToolButton({ name, pressed, className, onClick, children }: { name: string; pressed: boolean; className?: string; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" className={`label-tool${className ? ` ${className}` : ''}`} title={name} aria-label={name} aria-pressed={pressed} onClick={onClick}>
      {children}
    </button>
  )
}

/** The buttons of a label's toolbar, on a canvas or on the mobile layout's own screen: what styles its text, then what the label itself has. */
export function LabelTools({ isActive, onToggle, listActive, onToggleList, linkActive, onLink, onDelete, link, style, blocks, background, caption }: LabelToolsProps) {
  return (
    <>
      <div className="label-tool-section" role="group" aria-label="Text">
        <div className="label-tool-row">
          <div className="label-tool-group">
            {FORMATS.map(({ format, letter, name, className }) => (
              <ToolButton key={format} name={name} className={className} pressed={isActive(format)} onClick={() => onToggle(format)}>
                {letter}
              </ToolButton>
            ))}
            <ToolButton name="Link" pressed={linkActive} onClick={onLink}>
              <LinkIcon />
            </ToolButton>
          </div>
          <span className="toolbar-divider" aria-hidden="true" />
          <div className="label-tool-group">
            <LabelColor ink={style.ink} onChange={style.onInk} />
            <span className="toolbar-divider" aria-hidden="true" />
            <LabelSizes value={style.size} onChange={style.onSize} />
          </div>
        </div>
        <div className="label-tool-row">
          <div className="label-tool-group">
            {BLOCKS.map(({ kind, letter, name }) => (
              <ToolButton key={kind} name={name} pressed={blocks.isActive(kind)} onClick={() => blocks.onToggle(kind)}>
                {letter}
              </ToolButton>
            ))}
          </div>
          <span className="toolbar-divider" aria-hidden="true" />
          <div className="label-tool-group">
            <ToolButton name="Bulleted list" pressed={listActive('Bullet')} onClick={() => onToggleList('Bullet')}>
              <ListBulletIcon />
            </ToolButton>
            <ToolButton name="Numbered list" pressed={listActive('Numbered')} onClick={() => onToggleList('Numbered')}>
              <ListNumberIcon />
            </ToolButton>
            <ToolButton name="Task list" pressed={blocks.isActive('Task')} onClick={() => blocks.onToggle('Task')}>
              ☑
            </ToolButton>
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
      </div>
      <div className="label-tool-section label-own-tools" role="group" aria-label="Label">
        <div className="label-caption">
          <input
            className="label-caption-field"
            type="text"
            aria-label="Caption"
            placeholder="Caption"
            value={caption.value}
            maxLength={MAX_LABEL_TITLE}
            onChange={(e) => caption.onChange(e.target.value)}
            onKeyDown={(e) => {
              e.stopPropagation()
              // Back to the text, or done with the label.
              if (e.key === 'Enter') {
                e.preventDefault()
                caption.onDone()
              }
            }}
          />
          {caption.value && (
            // One click takes the caption off; the text being edited keeps its selection.
            <button type="button" className="label-caption-clear" aria-label="Remove caption" title="Remove caption" onClick={() => caption.onChange('')}>
              <CloseIcon size={12} />
            </button>
          )}
        </div>
        <button type="button" className="label-tool label-text-tool" aria-pressed={background.on} title="A panel behind the label, like a component's" onClick={background.onToggle}>
          Background
        </button>
        <button type="button" className="drawing-delete" aria-label="Delete label" title="Delete label (Del)" onClick={onDelete}>
          <TrashIcon size={15} />
        </button>
      </div>
    </>
  )
}

/** Screen pixels between the Color button and its palette. */
const POPUP_GAP = 6

interface LabelTrashProps {
  rect: Rect
  onDelete: () => void
  onPointerEnter?: () => void
  onPointerLeave?: () => void
}

/** The delete button of a selected or hovered label, in its top-right corner like a component's: everything else is done while editing it. */
export function LabelTrash({ rect, onDelete, onPointerEnter, onPointerLeave }: LabelTrashProps) {
  return (
    <button
      type="button"
      className="label-trash"
      aria-label="Delete label"
      title="Delete label (Del)"
      style={{ left: rect.x + rect.width, top: rect.y }}
      // Not a drag: the canvas would otherwise take the pointer and the click with it.
      onPointerDown={keepPress}
      onDoubleClick={(e) => e.stopPropagation()}
      onPointerEnter={onPointerEnter}
      onPointerLeave={onPointerLeave}
      onClick={onDelete}
    >
      <TrashIcon size={13} />
    </button>
  )
}

/** A label on a canvas and where it is shown right now. */
export interface PlacedLabel {
  label: LabelDetail
  rect: Rect
}

interface HoveredLabelTrashProps {
  hover: LabelHover
  labelAt: (id: string) => PlacedLabel | undefined
  onDelete: (id: string) => void
}

/** The delete button of the label the mouse is over: the only thing that renders as the mouse moves over labels. */
export function HoveredLabelTrash({ hover, labelAt, onDelete }: HoveredLabelTrashProps) {
  const id = useSyncExternalStore(hover.subscribe, hover.get)
  const placed = id ? labelAt(id) : undefined
  return placed ? <LabelTrash key={placed.label.id} rect={placed.rect} {...hover.barHover} onDelete={() => onDelete(placed.label.id)} /> : null
}
/** A label's box: its corner, the width its text wraps at, and the height it keeps with little text. */
export interface LabelBox {
  x: number
  y: number
  width: number
  minHeight: number | null
}

const MIN_BOX_HEIGHT = 20
const labelOnPage = (handles: HTMLElement | null, id?: string) => handles?.parentElement?.querySelector<HTMLElement>(id ? `[data-label="${id}"]` : '.label.editing') ?? null

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

  // As big as the label is on the page, which its text decides as much as its box.
  useLayoutEffect(() => {
    const el = labelOnPage(root.current, id)
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
    const el = labelOnPage(root.current, id)
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
      if (handle.includes('Right')) width = Math.max(MIN_LABEL_BOX, snap(right + dx) - at.x)
      if (handle.includes('Left')) {
        x = Math.min(snap(at.x + dx), right - MIN_LABEL_BOX)
        width = right - x
      }
      if (handle.includes('Bottom')) height = Math.max(MIN_BOX_HEIGHT, snap(bottom + dy) - at.y)
      if (handle.includes('Top')) {
        y = Math.min(snap(at.y + dy), bottom - MIN_BOX_HEIGHT)
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
