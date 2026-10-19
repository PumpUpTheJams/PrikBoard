import type { AssigneeKind } from '../api/types'
import { SparkleIcon } from '../board/icons'
import { initials } from './initials'

type Size = 'sm' | 'md' | 'lg'
const sparkleSize: Record<Size, number> = { sm: 12, md: 13, lg: 15 }

/** Anyone with a name: an assignee, or one of the board's people. */
interface Who {
  name: string
  kind: AssigneeKind
}

/** People show initials on graphite; agents show a sparkle on dark lilac. */
export function Avatar({ assignee, size = 'sm' }: { assignee: Who; size?: Size }) {
  const agent = assignee.kind === 'Agent'
  return (
    <span className={`avatar avatar-${size}${agent ? ' agent' : ''}`} title={agent ? `${assignee.name} (agent)` : assignee.name}>
      {agent ? <SparkleIcon size={sparkleSize[size]} /> : initials(assignee.name)}
    </span>
  )
}

/** Overlapping avatars: the first `max` shown, then a count of the rest. */
export function AvatarStack({ assignees, max = 2, size = 'sm' }: { assignees: (Who & { principalId: string })[]; max?: number; size?: Size }) {
  const shown = assignees.slice(0, max)
  const rest = assignees.length - shown.length
  return (
    <span className="avatar-stack">
      {shown.map((a) => (
        <Avatar key={a.principalId} assignee={a} size={size} />
      ))}
      {rest > 0 && (
        <span className={`avatar avatar-${size} more`} title={assignees.slice(max).map((a) => a.name).join(', ')}>
          +{rest}
        </span>
      )}
    </span>
  )
}
