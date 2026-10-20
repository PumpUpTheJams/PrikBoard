import { useEffect, useState } from 'react'
import type { NoteDetail } from '../api/types'
import { copyText } from './links'

/**
 * Copies a link that opens the post-it. `copied` says whether the last copy worked, for a moment after it; then it is
 * null again.
 */
export function useCopyLink(note: NoteDetail) {
  const [copied, setCopied] = useState<boolean | null>(null)

  useEffect(() => {
    if (copied === null) return
    const timer = setTimeout(() => setCopied(null), 1600)
    return () => clearTimeout(timer)
  }, [copied])

  const copy = async () => setCopied(await copyText(`${window.location.origin}${note.path}`))
  return { copied, copy }
}
