import { useId } from 'react'

interface NoticeDialogProps {
  title: string
  /** One paragraph each. */
  messages: string[]
  onClose: () => void
}

/** Something the user should read before going on, such as why an import was refused; closed with OK or Escape. */
export function NoticeDialog({ title, messages, onClose }: NoticeDialogProps) {
  const id = useId()

  return (
    <div
      className="confirm-backdrop"
      onPointerDown={(e) => e.target === e.currentTarget && onClose()}
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return
        e.stopPropagation()
        onClose()
      }}
    >
      <div className="confirm" role="alertdialog" aria-modal="true" aria-labelledby={`${id}-title`} aria-describedby={`${id}-message`}>
        <h2 id={`${id}-title`}>{title}</h2>
        <div id={`${id}-message`} className="notice-messages">
          {messages.map((message, i) => (
            <p key={i}>{message}</p>
          ))}
        </div>
        <div className="confirm-actions">
          <button type="button" className="primary" autoFocus onClick={onClose}>
            OK
          </button>
        </div>
      </div>
    </div>
  )
}
