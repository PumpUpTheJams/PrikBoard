import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'

/** Closes the menu a MenuItem is in. */
const CloseMenu = createContext<() => void>(() => {})

interface MenuProps {
  /** Accessible name of the trigger button. */
  label: string
  trigger: ReactNode
  triggerClassName?: string
  panelClassName?: string
  align?: 'start' | 'end'
  /** Menu content; call `close` after an action. */
  children: (close: () => void) => ReactNode
}

/** A trigger button with a popover panel that closes on Escape or a click outside. */
export function Menu({ label, trigger, triggerClassName, panelClassName, align = 'start', children }: MenuProps) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const close = () => setOpen(false)

  useEffect(() => {
    if (!open) return
    function onPointerDown(e: PointerEvent) {
      if (!ref.current?.contains(e.target as Node)) setOpen(false)
    }
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  return (
    <div className="menu" ref={ref}>
      <button
        type="button"
        className={triggerClassName}
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        {trigger}
      </button>
      {open && (
        <div className={`menu-panel menu-${align}${panelClassName ? ` ${panelClassName}` : ''}`} role="menu" aria-label={label}>
          <CloseMenu.Provider value={close}>{children(close)}</CloseMenu.Provider>
        </div>
      )}
    </div>
  )
}

interface MenuItemProps {
  icon?: ReactNode
  title?: string
  danger?: boolean
  /** Makes it one of a set to pick from (menuitemradio), ticked when true. */
  checked?: boolean
  onClick: () => void
  children: ReactNode
}

interface MenuToggleProps {
  icon?: ReactNode
  title?: string
  checked: boolean
  onChange: (checked: boolean) => void
  children: ReactNode
}

/** A switch in a Menu (menuitemcheckbox). The menu stays open, so the change shows at once. */
export function MenuToggle({ icon, title, checked, onChange, children }: MenuToggleProps) {
  return (
    <button type="button" role="menuitemcheckbox" aria-checked={checked} className="menu-item" title={title} onClick={() => onChange(!checked)}>
      {icon}
      <span className="menu-item-text">{children}</span>
      <span className="menu-switch" aria-hidden="true" />
    </button>
  )
}

/** A button in a Menu that closes the menu, then runs `onClick`. */
export function MenuItem({ icon, title, danger, checked, onClick, children }: MenuItemProps) {
  const close = useContext(CloseMenu)
  return (
    <button
      type="button"
      role={checked === undefined ? 'menuitem' : 'menuitemradio'}
      aria-checked={checked}
      className={`menu-item${danger ? ' danger' : ''}`}
      title={title}
      onClick={() => {
        close()
        onClick()
      }}
    >
      {icon}
      {children}
    </button>
  )
}
