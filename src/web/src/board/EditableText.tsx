import { useLayoutEffect, useRef, useState, type RefObject } from 'react'

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
  inputRef?: RefObject<HTMLTextAreaElement | null>
}

/**
 * A growing text area that saves on blur (and on Enter unless multiline). Escape cancels.
 * While it isn't focused it follows the server's value, so edits made by agents show up.
 */
export function EditableText({ value, onSave, label, className, maxLength, multiline, placeholder, autoFocus, readOnly, inputRef }: EditableTextProps) {
  const [draft, setDraft] = useState<string | null>(null)
  const cancelled = useRef(false)
  const local = useRef<HTMLTextAreaElement | null>(null)
  const shown = draft ?? value

  // Grow with the text.
  useLayoutEffect(() => {
    const el = local.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${el.scrollHeight}px`
  }, [shown])

  return (
    <textarea
      ref={(el) => {
        local.current = el
        if (inputRef) inputRef.current = el
      }}
      className={`editable ${className}`}
      aria-label={label}
      rows={1}
      value={shown}
      maxLength={maxLength}
      placeholder={readOnly ? undefined : placeholder}
      autoFocus={autoFocus}
      readOnly={readOnly}
      onFocus={(e) => {
        setDraft(value)
        if (autoFocus) e.target.select()
      }}
      onChange={(e) => setDraft(multiline ? e.target.value : e.target.value.replace(/\n/g, ' '))}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.stopPropagation()
          cancelled.current = true
          e.currentTarget.blur()
        } else if (e.key === 'Enter' && !multiline) {
          e.preventDefault()
          e.currentTarget.blur()
        }
      }}
      onBlur={() => {
        const next = (draft ?? value).trim()
        if (!cancelled.current && next !== value.trim()) onSave(next)
        cancelled.current = false
        setDraft(null)
      }}
    />
  )
}
