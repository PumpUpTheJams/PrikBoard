import { useEffect, useState } from 'react'
import type { CardDetail } from '../api/types'
import { copyText } from './links'

/**
 * Puts text on the clipboard (copyText). `copied` says whether the last copy worked and `copiedText` is what it put there
 * (null when it failed). With `resetAfter` (ms) both go back to null that long after a copy; otherwise they stay.
 */
export function useCopy(resetAfter?: number) {
  const [last, setLast] = useState<{ text: string; ok: boolean } | null>(null)
  const copied = last?.ok ?? null
  const copiedText = last?.ok ? last.text : null

  useEffect(() => {
    if (copied === null || resetAfter === undefined) return
    const timer = setTimeout(() => setLast(null), resetAfter)
    return () => clearTimeout(timer)
  }, [copied, copiedText, resetAfter])

  const copy = async (text: string) => setLast({ text, ok: await copyText(text) })
  /** Forgets the last copy, so a Copy button reads "Copy" again. */
  const reset = () => setLast(null)
  return { copied, copiedText, copy, reset }
}

/**
 * Copies a link that opens the card. `copied` says whether the last copy worked, for a moment after it; then it is
 * null again.
 */
export function useCopyLink(card: CardDetail) {
  const { copied, copy } = useCopy(1600)
  return { copied, copy: () => copy(`${window.location.origin}${card.path}`) }
}
