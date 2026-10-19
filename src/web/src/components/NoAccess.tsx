import { SadPostItIcon } from '../board/icons'

interface NoAccessProps {
  title?: string
  message?: string
  onBack: () => void
}

/**
 * Shown for a link to a board you can't open. The API answers 404 both for boards that don't exist and for boards
 * you aren't a member of, so the page can't (and shouldn't) tell the two apart.
 */
export function NoAccess({
  title = "This board isn't here",
  message = "It doesn't exist, or you don't have access to it. Ask one of its owners for an invite link.",
  onBack,
}: NoAccessProps) {
  return (
    <div className="empty no-access">
      <span className="board-tile large" aria-hidden="true">
        <SadPostItIcon size={28} />
      </span>
      <h2>{title}</h2>
      <p>{message}</p>
      <button type="button" className="ghost" onClick={onBack}>
        Go to your boards
      </button>
    </div>
  )
}
