import { useLayoutEffect, useRef, useState, type PointerEvent } from 'react'
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
export function LabelSizes({ value, onChange }: { value: LabelSize; onChange: (size: LabelSize) => void }) {
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
  ink: InkColor
  size: LabelSize
  isActive: (format: LabelFormat) => boolean
  onToggle: (format: LabelFormat) => void
  listActive: (kind: LabelListKind) => boolean
  onToggleList: (kind: LabelListKind) => void
  linkActive: boolean
  onLink: () => void
  onInk: (ink: InkColor) => void
  onSize: (size: LabelSize) => void
  onDelete: () => void
  link?: LabelLinkField | null
}

/** Screen pixels kept between the toolbar and its label, and between it and the edge of the view. */
const TOOLBAR_GAP = 12
const TOOLBAR_MARGIN = 8

/** The toolbar above a selected label or one being edited, at the same size on screen at any zoom. */
export function LabelToolbar(props: LabelToolbarProps) {
  const { rect, zoom, ink, size, isActive, onToggle, listActive, onToggleList, linkActive, onLink, onInk, onSize, onDelete, link } = props
  const toolbar = useRef<HTMLDivElement>(null)
  /** How far it slides to stay in view, in screen pixels, and whether it goes below the label for lack of room above. */
  const [place, setPlace] = useState({ x: 0, below: 0 })
  useLayoutEffect(() => {
    const el = toolbar.current!
    const scroller = el.closest('.board-scroll, .card-scroll')
    if (!scroller) return
    const view = scroller.getBoundingClientRect()
    const box = el.getBoundingClientRect()
    const left = box.left - place.x
    const right = left + box.width
    const edge = view.left + scroller.clientWidth - TOOLBAR_MARGIN
    const x = left < view.left + TOOLBAR_MARGIN ? view.left + TOOLBAR_MARGIN - left : right > edge ? Math.max(view.left + TOOLBAR_MARGIN - left, edge - right) : 0
    // The label being edited grows, so its height comes from the page there.
    const height = rect.height || el.parentElement!.offsetHeight
    const below = place.below || box.top < view.top + TOOLBAR_MARGIN ? height * zoom + box.height + 2 * TOOLBAR_GAP : 0
    if (x !== place.x || below !== place.below) setPlace({ x, below })
  })
  return (
    <div
      ref={toolbar}
      className={`drawing-toolbar label-toolbar${link ? ' with-link' : ''}`}
      role="toolbar"
      aria-label="Label"
      style={{ left: rect.x, top: rect.y, transform: `scale(${1 / zoom}) translate(${place.x}px, calc(-100% - ${TOOLBAR_GAP}px + ${place.below}px))` }}
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
      <InkSwatches value={ink} onChange={onInk} />
      <span className="toolbar-divider" aria-hidden="true" />
      <LabelSizes value={size} onChange={onSize} />
      <span className="toolbar-divider" aria-hidden="true" />
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
