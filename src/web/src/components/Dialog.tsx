import { useEffect, useId, useRef, type ReactNode } from 'react'
import { CloseIcon } from '../board/icons'

interface DialogProps {
  title: string
  /** Shown under the title, e.g. the board the dialog is about. */
  subtitle?: string
  onClose: () => void
  children: ReactNode
  wide?: boolean
}

/** A modal panel in the Night shift style: closes on Escape, on the close button or on a click outside. */
export function Dialog({ title, subtitle, onClose, children, wide }: DialogProps) {
  const id = useId()
  const panel = useRef<HTMLDivElement>(null)

  useEffect(() => {
    panel.current?.focus()
  }, [])

  return (
    <div
      className="dialog-backdrop"
      onPointerDown={(e) => e.target === e.currentTarget && onClose()}
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return
        e.stopPropagation()
        onClose()
      }}
    >
      <div ref={panel} className={`dialog${wide ? ' wide' : ''}`} role="dialog" aria-modal="true" aria-labelledby={id} tabIndex={-1}>
        <header className="dialog-header">
          <div>
            <h2 id={id}>{title}</h2>
            {subtitle && <p className="dialog-subtitle">{subtitle}</p>}
          </div>
          <button type="button" className="icon-button" aria-label="Close" onClick={onClose}>
            <CloseIcon />
          </button>
        </header>
        {children}
      </div>
    </div>
  )
}
