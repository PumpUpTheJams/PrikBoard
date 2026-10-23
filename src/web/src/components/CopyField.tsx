import type { useCopy } from '../board/useCopy'

interface CopyFieldProps {
  /** Accessible name of the field. */
  label: string
  value: string
  /** Copies the value; the button reads "Copied" while it holds this value. */
  copier: Pick<ReturnType<typeof useCopy>, 'copiedText' | 'copy'>
}

/** A read-only field (selected on focus) with a Copy button next to it. */
export function CopyField({ label, value, copier }: CopyFieldProps) {
  return (
    <div className="copy-field">
      <input readOnly aria-label={label} value={value} onFocus={(e) => e.target.select()} />
      <button type="button" className="ghost small" onClick={() => void copier.copy(value)}>
        {copier.copiedText === value ? 'Copied' : 'Copy'}
      </button>
    </div>
  )
}
