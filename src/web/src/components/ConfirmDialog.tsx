import { useId } from 'react'
import { TrashIcon } from '../board/icons'

interface ConfirmDialogProps {
  title: string
  message: string
  confirmLabel: string
  onConfirm: () => void
  onCancel: () => void
}

/**
 * An in-app confirmation for destructive actions. Used instead of window.confirm, which embedded browsers
 * (such as the Claude desktop browser pane) may suppress, silently answering "cancel".
 */
export function ConfirmDialog({ title, message, confirmLabel, onConfirm, onCancel }: ConfirmDialogProps) {
  const id = useId()

  return (
    <div
      className="confirm-backdrop"
      onPointerDown={(e) => e.target === e.currentTarget && onCancel()}
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return
        e.stopPropagation()
        onCancel()
      }}
    >
      <div className="confirm" role="alertdialog" aria-modal="true" aria-labelledby={`${id}-title`} aria-describedby={`${id}-message`}>
        <h2 id={`${id}-title`}>{title}</h2>
        <p id={`${id}-message`}>{message}</p>
        <div className="confirm-actions">
          <button type="button" className="ghost" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="destructive" autoFocus onClick={onConfirm}>
            <TrashIcon />
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}

/** Asks before deleting a post-it, from the board (Delete key) or from the expanded post-it. */
export function ConfirmDeleteNote({ title, onConfirm, onCancel }: { title: string; onConfirm: () => void; onCancel: () => void }) {
  return (
    <ConfirmDialog
      title="Delete this post-it?"
      message={`"${title}" will be removed from the board. This can't be undone.`}
      confirmLabel="Delete"
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  )
}
