import { useRef, useState, type FocusEvent, type KeyboardEvent, type Ref } from 'react'

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
  /** The field itself, for focusing it from outside (a field in a window that is still hidden can't autofocus). */
  fieldRef?: Ref<HTMLInputElement & HTMLTextAreaElement>
}

/**
 * Text edited in place that saves on blur (and on Enter unless multiline). Escape cancels. Single-line text stays one
 * line, scrolling sideways with the text cursor; multiline text fills its box and scrolls vertically inside it.
 * While it isn't focused it follows the server's value, so edits made by agents show up.
 */
export function EditableText(props: EditableTextProps) {
  const { value, onSave, label, className, maxLength, multiline, placeholder, autoFocus, readOnly, fieldRef } = props
  const [draft, setDraft] = useState<string | null>(null)
  const cancelled = useRef(false)

  const field = {
    ref: fieldRef,
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
    },
  }

  return multiline ? (
    <textarea {...field} onChange={(e) => setDraft(e.target.value)} />
  ) : (
    <input {...field} onChange={(e) => setDraft(e.target.value)} />
  )
}
