import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { CheckIcon, ChevronDownIcon } from '../board/icons'

export interface SelectOption<T extends string> {
  value: T
  label: string
  /** A second, muted line under the label. */
  description?: string
}

interface SelectProps<T extends string> {
  /** Accessible name of the trigger. */
  label: string
  value: T
  options: SelectOption<T>[]
  onChange: (value: T) => void
  disabled?: boolean
  className?: string
}

/**
 * A dropdown in the style of the app's menus. The panel is fixed to the viewport, so it is not
 * clipped by a scrolling dialog; it closes on a choice, Escape, a click outside, scrolling or resizing.
 */
export function Select<T extends string>({ label, value, options, onChange, disabled, className }: SelectProps<T>) {
  const [open, setOpen] = useState(false)
  const [place, setPlace] = useState<{ top: number; left: number; minWidth: number } | null>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)
  const current = options.find((o) => o.value === value)

  useLayoutEffect(() => {
    if (!open || !trigger.current) return
    const rect = trigger.current.getBoundingClientRect()
    const width = panel.current?.offsetWidth ?? rect.width
    const height = panel.current?.offsetHeight ?? 0
    // Prefer below and right-aligned with the trigger; flip up or shift when it would leave the screen.
    const below = rect.bottom + 6 + height <= window.innerHeight - 8
    setPlace({
      top: below ? rect.bottom + 6 : Math.max(8, rect.top - 6 - height),
      left: Math.min(Math.max(8, rect.right - width), window.innerWidth - width - 8),
      minWidth: rect.width,
    })
  }, [open])

  useEffect(() => {
    if (!open) return
    panel.current?.querySelector<HTMLButtonElement>('[aria-checked="true"]')?.focus()
    function onPointerDown(e: PointerEvent) {
      const target = e.target as Node
      if (!panel.current?.contains(target) && !trigger.current?.contains(target)) setOpen(false)
    }
    function onScroll(e: Event) {
      if (!panel.current?.contains(e.target as Node)) setOpen(false)
    }
    const close = () => setOpen(false)
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', close)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('scroll', onScroll, true)
      window.removeEventListener('resize', close)
    }
  }, [open])

  function closeAndFocus() {
    setOpen(false)
    trigger.current?.focus()
  }

  function onPanelKeyDown(e: KeyboardEvent) {
    const items = [...(panel.current?.querySelectorAll<HTMLButtonElement>('button') ?? [])]
    const index = items.indexOf(document.activeElement as HTMLButtonElement)
    if (e.key === 'Escape') {
      // Close only the dropdown, not the dialog around it.
      e.stopPropagation()
      closeAndFocus()
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      const next = (index + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length
      items[next]?.focus()
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault()
      items[e.key === 'Home' ? 0 : items.length - 1]?.focus()
    } else if (e.key === 'Tab') {
      setOpen(false)
    }
  }

  return (
    <>
      <button
        ref={trigger}
        type="button"
        className={`select-trigger${className ? ` ${className}` : ''}`}
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault()
            setOpen(true)
          }
        }}
      >
        <span className="select-value">{current?.label ?? value}</span>
        <ChevronDownIcon />
      </button>
      {open && (
        <div
          ref={panel}
          className="menu-panel select-panel"
          role="menu"
          aria-label={label}
          style={place ? { top: place.top, left: place.left, minWidth: place.minWidth } : { visibility: 'hidden' }}
          onKeyDown={onPanelKeyDown}
        >
          {options.map((option) => (
            <button
              key={option.value}
              type="button"
              role="menuitemradio"
              aria-checked={option.value === value}
              className="menu-item select-option"
              onClick={() => {
                closeAndFocus()
                if (option.value !== value) onChange(option.value)
              }}
            >
              <span className="select-option-text">
                <span>{option.label}</span>
                {option.description && <span className="select-option-detail">{option.description}</span>}
              </span>
              {option.value === value && <CheckIcon />}
            </button>
          ))}
        </div>
      )}
    </>
  )
}
