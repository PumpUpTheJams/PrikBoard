import { useEffect, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { ChevronLeftIcon, PlusIcon } from '../board/icons'
import { back } from './nav'

interface TopBarProps {
  /** The back button: where it goes up to when there is no screen to go back to, and what it says (or an icon only). */
  up?: { to: string; label: string; text?: string }
  /** A small line above the title, e.g. the board above a bucket. */
  context?: ReactNode
  title?: ReactNode
  /** Buttons at the end. */
  children?: ReactNode
}

/** The bar at the top of a mobile screen: back, what the screen shows, and its actions. */
export function TopBar({ up, context, title, children }: TopBarProps) {
  return (
    <header className="m-bar">
      {up && (
        <button type="button" className={`m-back${up.text ? ' with-text' : ''}`} aria-label={up.label} onClick={() => back(up.to)}>
          <ChevronLeftIcon />
          {up.text}
        </button>
      )}
      <div className="m-bar-title">
        {context && <span className="m-bar-context">{context}</span>}
        {title && <span className="m-bar-name">{title}</span>}
      </div>
      {children}
    </header>
  )
}

/** A round button for the top bar, with only an icon. */
export function BarButton({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" className="m-bar-button" aria-label={label} title={label} onClick={onClick}>
      {children}
    </button>
  )
}

/** The floating button at the bottom right of a screen that adds something (a card, a component). */
export function AddButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button type="button" className="m-fab" onClick={onClick}>
      <PlusIcon size={20} />
      {label}
    </button>
  )
}

interface SheetProps {
  /** The sheet's heading, and its name for screen readers. */
  title: string
  /** Shown at the end of the heading, e.g. the card's readable id. */
  aside?: ReactNode
  onClose: () => void
  children: ReactNode
}

/** A sheet that slides up from the bottom over the screen; a tap on the dimmed screen above it, or Escape, closes it. */
export function Sheet({ title, aside, onClose, children }: SheetProps) {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  return createPortal(
    <div className="m-sheet-layer">
      <button type="button" className="m-sheet-backdrop" aria-label="Close" onClick={onClose} />
      <section className="m-sheet" role="dialog" aria-modal="true" aria-label={title}>
        <span className="m-sheet-grip" aria-hidden="true" />
        <div className="m-sheet-head">
          <h2>{title}</h2>
          {aside}
        </div>
        {children}
      </section>
    </div>,
    document.body,
  )
}
