import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { CheckIcon, CloseIcon, PencilIcon, SearchIcon } from '../board/icons'
import { useMobileLayout } from '../layoutMode'

export interface PickerChip {
  key: string
  label: string
  leading?: ReactNode
  /** Extra class names, e.g. a tag's color. */
  className?: string
  onRemove: () => void
}

export interface PickerRow {
  key: string
  label: string
  /** A second, muted line. */
  detail?: string
  leading?: ReactNode
  /** Shown on the right, e.g. how many cards use a tag. */
  meta?: ReactNode
  /** A tick in front: the item is on the card (or in the filter). */
  checked?: boolean
  disabled?: boolean
  onSelect: () => void
  /** Shown at the end of the row, before the edit button, e.g. a tag's color circle. */
  trailing?: ReactNode
  /** Shows an edit button on the row. */
  onEdit?: () => void
  /** Replaces the row while it is being edited. */
  editor?: ReactNode
}

interface PickerWindowProps {
  /** Screen rectangle of what opened the window; it opens below (or above) it. */
  anchor: DOMRect
  label: string
  placeholder: string
  query: string
  onQueryChange: (query: string) => void
  chips?: PickerChip[]
  rows: PickerRow[]
  /** A muted line under the list, e.g. "3 more: type to find them". */
  footer?: string
  empty?: string
  /** Keeps the window open while something on top of it (a confirmation) has the focus. */
  holdOpen?: boolean
  /** What the window belongs to (its toggle button, say): pressing there doesn't count as clicking outside. */
  owner?: RefObject<HTMLElement | null>
  /** Something that opens over the window, part of it (a palette): pressing there doesn't close the window. */
  overlay?: ReactNode
  onClose: () => void
}

const WIDTH = 300

/**
 * A small search window next to what opened it: a search field, the current picks as chips, and a list to pick from.
 * Keyboard: arrows move through the list, Enter picks, Escape closes (and only the window), Backspace in an empty
 * field removes the last chip. It is portalled to the page, so the board's zoom doesn't scale or clip it, and focus
 * stays in its field, so the board's shortcuts (Delete removes the selected card) never fire while it is open.
 */
export function PickerWindow(props: PickerWindowProps) {
  const { anchor, label, placeholder, query, onQueryChange, chips = [], rows, footer, empty, holdOpen, owner, overlay, onClose } = props
  /** On a phone it is a sheet along the bottom of the screen (above the keyboard), not a window next to the anchor. */
  const sheet = useMobileLayout()
  const id = useId()
  const panel = useRef<HTMLDivElement>(null)
  const input = useRef<HTMLInputElement>(null)
  const [active, setActive] = useState(0)
  const [place, setPlace] = useState<{ top: number; left: number } | null>(null)
  const selectable = rows.filter((r) => !r.editor && !r.disabled)
  const activeRow = selectable[Math.min(active, selectable.length - 1)]

  useLayoutEffect(() => {
    // A sheet has its place in the CSS.
    if (sheet) return
    const height = panel.current?.offsetHeight ?? 0
    // Below the anchor, left-aligned; up when there is no room below, and always on screen.
    const below = anchor.bottom + 6 + height <= window.innerHeight - 8
    setPlace({
      top: below ? anchor.bottom + 6 : Math.max(8, anchor.top - 6 - height),
      left: Math.min(Math.max(8, anchor.left), window.innerWidth - WIDTH - 8),
    })
  }, [anchor, rows.length, chips.length, sheet])

  // Picking or editing moves focus to buttons and fields inside; give it back to the search field afterwards.
  // (It is hidden until placed, and hidden fields can't take focus.)
  const editing = rows.some((r) => r.editor)
  const placed = sheet || place !== null
  useEffect(() => {
    if (placed && !editing && !holdOpen) input.current?.focus({ preventScroll: true })
  }, [placed, editing, holdOpen])

  useEffect(() => {
    if (holdOpen) return
    function onPointerDown(e: PointerEvent) {
      const target = e.target as Node
      if (!panel.current?.contains(target) && !owner?.current?.contains(target)) onClose()
    }
    function onScroll(e: Event) {
      if (!panel.current?.contains(e.target as Node)) onClose()
    }
    document.addEventListener('pointerdown', onPointerDown, true)
    // A window closes when the page scrolls or resizes, since it would no longer sit next to what opened it. A sheet
    // stays open (the keyboard resizes the page).
    if (sheet) return () => document.removeEventListener('pointerdown', onPointerDown, true)
    document.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', onClose)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true)
      document.removeEventListener('scroll', onScroll, true)
      window.removeEventListener('resize', onClose)
    }
  }, [holdOpen, owner, onClose, sheet])

  return createPortal(
    <div
      ref={panel}
      className={sheet ? 'picker sheet' : 'picker'}
      role="dialog"
      aria-label={label}
      style={sheet ? undefined : place ? { top: place.top, left: place.left, width: WIDTH } : { visibility: 'hidden', width: WIDTH }}
      onKeyDown={(e) => {
        // Inside the window, Escape and Tab belong to it, not to the card or board around it.
        if (e.key === 'Escape') {
          e.stopPropagation()
          if (!editing) onClose()
        } else if (e.key === 'Tab' && !editing) {
          onClose()
        }
      }}
    >
      <div className="picker-search">
        <SearchIcon />
        <input
          ref={input}
          role="combobox"
          aria-expanded="true"
          aria-controls={`${id}-list`}
          aria-activedescendant={activeRow ? `${id}-${activeRow.key}` : undefined}
          aria-label={label}
          placeholder={placeholder}
          value={query}
          maxLength={100}
          onChange={(e) => {
            onQueryChange(e.target.value)
            setActive(0)
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
              e.preventDefault()
              const count = selectable.length
              if (count > 0) setActive((a) => (Math.min(a, count - 1) + (e.key === 'ArrowDown' ? 1 : -1) + count) % count)
            } else if (e.key === 'Enter') {
              e.preventDefault()
              activeRow?.onSelect()
            } else if (e.key === 'Backspace' && query === '' && chips.length > 0) {
              e.preventDefault()
              chips[chips.length - 1].onRemove()
            }
          }}
        />
      </div>
      {chips.length > 0 && (
        <div className="picker-chips">
          {chips.map((chip) => (
            <span key={chip.key} className={`picker-chip${chip.className ?? ''}`}>
              {chip.leading}
              <span className="picker-chip-label">{chip.label}</span>
              <button
                type="button"
                className="chip-remove"
                aria-label={`Remove ${chip.label}`}
                onPointerDown={(e) => e.preventDefault()}
                onClick={chip.onRemove}
              >
                <CloseIcon size={10} />
              </button>
            </span>
          ))}
        </div>
      )}
      <ul className="picker-list" id={`${id}-list`} role="listbox" aria-label={label} aria-multiselectable="true">
        {rows.map((row) =>
          row.editor ? (
            <li key={row.key} className="picker-row editing">
              {row.editor}
            </li>
          ) : (
            <li
              key={row.key}
              id={`${id}-${row.key}`}
              role="option"
              aria-selected={!!row.checked}
              aria-disabled={row.disabled || undefined}
              className={`picker-row${row === activeRow ? ' active' : ''}`}
            >
              <button
                type="button"
                className="picker-pick"
                tabIndex={-1}
                disabled={row.disabled}
                // Keep focus (and the keyboard) in the search field.
                onPointerDown={(e) => e.preventDefault()}
                onClick={row.onSelect}
              >
                <span className="picker-check" aria-hidden="true">
                  {row.checked && <CheckIcon size={13} />}
                </span>
                {row.leading}
                <span className="picker-text">
                  <span className="picker-label">{row.label}</span>
                  {row.detail && <span className="picker-detail">{row.detail}</span>}
                </span>
                {row.meta !== undefined && <span className="picker-meta">{row.meta}</span>}
              </button>
              {row.trailing}
              {row.onEdit && (
                <button
                  type="button"
                  className="picker-edit"
                  tabIndex={-1}
                  aria-label={`Edit ${row.label}`}
                  title="Rename or delete"
                  onPointerDown={(e) => e.preventDefault()}
                  onClick={row.onEdit}
                >
                  <PencilIcon />
                </button>
              )}
            </li>
          ),
        )}
        {rows.length === 0 && empty && <li className="picker-empty">{empty}</li>}
      </ul>
      {footer && <p className="picker-footer">{footer}</p>}
      {overlay}
    </div>,
    document.body,
  )
}
