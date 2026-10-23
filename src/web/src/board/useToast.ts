import { useCallback, useState } from 'react'
import type { ToastMessage } from '../components/Toast'

/** A short confirmation at the bottom of the board (a link was added, an image went into a card, an undo). */
export function useToast() {
  const [toast, setToast] = useState<ToastMessage | null>(null)
  const clearToast = useCallback(() => setToast(null), [])
  /** Shows a toast; a new one replaces the last, even with the same text. */
  const notify = (message: string | Omit<ToastMessage, 'id'>) =>
    setToast({ id: Date.now(), ...(typeof message === 'string' ? { text: message } : message) })
  return { toast, clearToast, notify }
}

export type Notify = ReturnType<typeof useToast>['notify']
