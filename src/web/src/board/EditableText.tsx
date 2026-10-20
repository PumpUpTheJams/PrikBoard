import { useRef, useState, type FocusEvent, type KeyboardEvent } from 'react'

interface EditableTextProps {
  value: string
  onSave: (value: string) => void
  label: string
  className: string
  maxLength: number
  multiline?: boolean
  placeholder?: string
  autoFocus?: boolean
  readOnly?: boolean
  /** Called when the field loses focus, after any save. */
  onDone?: () => void
}

/**
 * Text edited in place that saves on blur (and on Enter unless multiline). Escape cancels. Single-line text stays one
 * line, scrolling sideways with the text cursor; multiline text fills its box and scrolls vertically inside it.
 * While it isn't focused it follows the server's value, so edits made by agents show up.
 */
export function EditableText({ value, onSave, label, className, maxLength, multiline, placeholder, autoFocus, readOnly, onDone }: EditableTextProps) {
  const [draft, setDraft] = useState<string | null>(null)
  const cancelled = useRef(false)

  const field = {
    className: `editable ${className}`,
    'aria-label': label,
    value: draft ?? value,
    maxLength,
    placeholder: readOnly ? undefined : placeholder,
    autoFocus,
    readOnly,
    onFocus: (e: FocusEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      setDraft(value)
      if (autoFocus) e.target.select()
    },
    onKeyDown: (e: KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        cancelled.current = true
        e.currentTarget.blur()
      } else if (e.key === 'Enter' && !multiline) {
        e.preventDefault()
        e.currentTarget.blur()
      }
    },
    onBlur: () => {
      const next = (draft ?? value).trim()
      if (!cancelled.current && next !== value.trim()) onSave(next)
      cancelled.current = false
      setDraft(null)
      onDone?.()
    },
  }

  return multiline ? (
    <textarea {...field} onChange={(e) => setDraft(e.target.value)} />
  ) : (
    <input {...field} onChange={(e) => setDraft(e.target.value)} />
  )
}
