import { useEffect } from 'react'
import { CheckIcon } from '../board/icons'

/** A short confirmation; a new `id` shows it again even when the text is the same. */
export interface ToastMessage {
  id: number
  text: string
}

/** How long a toast stays up. */
const DURATION = 2200

/**
 * A small confirmation at the bottom of the board, gone after a moment. The region is always there, so screen readers
 * announce each new message; it never takes the pointer.
 */
export function Toast({ message, onDone }: { message: ToastMessage | null; onDone: () => void }) {
  useEffect(() => {
    if (!message) return
    const timer = setTimeout(onDone, DURATION)
    return () => clearTimeout(timer)
  }, [message, onDone])

  return (
    <div className="toast-region" role="status" aria-live="polite">
      {message && (
        <div key={message.id} className="toast">
          <CheckIcon size={14} />
          {message.text}
        </div>
      )}
    </div>
  )
}
