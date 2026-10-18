import type { Assignee } from '../api/types'
import { SparkleIcon } from '../board/icons'

/** "Jaap V." becomes "JV", "Claude" becomes "CL". */
function initials(name: string) {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'
  const letters = words.length > 1 ? words[0][0] + words[words.length - 1][0] : words[0].slice(0, 2)
  return letters.toUpperCase()
}

type Size = 'sm' | 'md' | 'lg'
const sparkleSize: Record<Size, number> = { sm: 12, md: 13, lg: 15 }

/** People show initials on graphite; agents show a sparkle on dark lilac. */
export function Avatar({ assignee, size = 'sm' }: { assignee: Assignee; size?: Size }) {
  const agent = assignee.kind === 'Agent'
  return (
    <span className={`avatar avatar-${size}${agent ? ' agent' : ''}`} title={agent ? `${assignee.name} (agent)` : assignee.name}>
      {agent ? <SparkleIcon size={sparkleSize[size]} /> : initials(assignee.name)}
    </span>
  )
}

/** Overlapping avatars: the first `max` shown, then a count of the rest. */
export function AvatarStack({ assignees, max = 2, size = 'sm' }: { assignees: Assignee[]; max?: number; size?: Size }) {
  const shown = assignees.slice(0, max)
  const rest = assignees.length - shown.length
  return (
    <span className="avatar-stack">
      {shown.map((a) => (
        <Avatar key={`${a.kind}:${a.name}`} assignee={a} size={size} />
      ))}
      {rest > 0 && (
        <span className={`avatar avatar-${size} more`} title={assignees.slice(max).map((a) => a.name).join(', ')}>
          +{rest}
        </span>
      )}
    </span>
  )
}
