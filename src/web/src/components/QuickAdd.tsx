import { useState, type FormEvent } from 'react'

interface QuickAddProps {
  label: string
  placeholder: string
  /** Resolves to true when the item was added; the input then clears and stays open for the next one. */
  onAdd: (text: string) => Promise<boolean>
}

export function QuickAdd({ label, placeholder, onAdd }: QuickAddProps) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    const value = text.trim()
    if (!value || busy) return

    setBusy(true)
    if (await onAdd(value)) setText('')
    setBusy(false)
  }

  if (!open) {
    return (
      <button type="button" className="quick-add-toggle" onClick={() => setOpen(true)}>
        + {label}
      </button>
    )
  }

  return (
    <form className="quick-add" onSubmit={submit}>
      <input
        autoFocus
        value={text}
        placeholder={placeholder}
        aria-label={label}
        // readOnly rather than disabled: a disabled input drops focus, which breaks typing the next item.
        readOnly={busy}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => e.key === 'Escape' && setOpen(false)}
      />
      <button type="submit" disabled={busy || !text.trim()}>
        Add
      </button>
      <button type="button" className="ghost" onClick={() => setOpen(false)}>
        Cancel
      </button>
    </form>
  )
}
