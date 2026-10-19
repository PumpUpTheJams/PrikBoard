import { useEffect, useState } from 'react'
import { CheckIcon } from '../board/icons'

/** A short confirmation; a new `id` shows it again even when the text is the same. */
export interface ToastMessage {
  id: number
  text: string
  /** Something that didn't work out (a refused undo). */
  tone?: 'warn'
  /** A button in the toast, e.g. Undo after a delete. */
  action?: { label: string; onAction: () => void }
}

/** How long a toast stays up; one with a button stays longer, and while the pointer is on it. */
const DURATION = 2200
const ACTION_DURATION = 5000

/**
 * A small confirmation at the bottom of the board, gone after a moment. The region is always there, so screen readers
 * announce each new message; only a toast with a button takes the pointer.
 */
export function Toast({ message, onDone }: { message: ToastMessage | null; onDone: () => void }) {
  /** The toast the pointer is on: it stays until the pointer leaves. */
  const [hovered, setHovered] = useState<number | null>(null)
  const hover = message !== null && hovered === message.id

  useEffect(() => {
    if (!message || hover) return
    // A button needs time to be reached, and a warning time to be read.
    const timer = setTimeout(onDone, message.action || message.tone ? ACTION_DURATION : DURATION)
    return () => clearTimeout(timer)
  }, [message, hover, onDone])

  return (
    <div className="toast-region" role="status" aria-live="polite">
      {message && (
        <div
          key={message.id}
          className={`toast${message.tone ? ` ${message.tone}` : ''}${message.action ? ' has-action' : ''}`}
          onPointerEnter={() => setHovered(message.id)}
          onPointerLeave={() => setHovered(null)}
        >
          {message.tone !== 'warn' && <CheckIcon size={14} />}
          {message.text}
          {message.action && (
            <button
              type="button"
              className="toast-action"
              onClick={() => {
                message.action!.onAction()
                onDone()
              }}
            >
              {message.action.label}
            </button>
          )}
        </div>
      )}
    </div>
  )
}
