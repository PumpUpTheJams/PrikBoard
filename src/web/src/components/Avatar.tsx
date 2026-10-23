import { useState } from 'react'
import type { AssigneeKind } from '../api/types'
import { SparkleIcon, UserIcon } from '../board/icons'
import { initials } from './initials'

type Size = 'sm' | 'md' | 'lg'
const sparkleSize: Record<Size, number> = { sm: 12, md: 13, lg: 15 }

/** Anyone with a name: an assignee, or one of the board's people. */
interface Who {
  name: string
  kind: AssigneeKind
  avatarUrl: string | null
}

/**
 * What goes inside an avatar circle: a person's chosen picture, or their initials when they have none (or it fails to
 * load). The circle itself is the caller's.
 */
export function Face({ name, avatarUrl }: { name: string; avatarUrl: string | null }) {
  const [failed, setFailed] = useState<string | null>(null)
  if (avatarUrl && failed !== avatarUrl) {
    return <img className="avatar-img" src={avatarUrl} alt="" draggable={false} onError={() => setFailed(avatarUrl)} />
  }
  return initials(name)
}

/**
 * People show their picture or initials on graphite; agents show a sparkle on dark lilac. Without a name (a public
 * board that hides its people from visitors) it is an anonymous circle. `working`: an agent is at work on the card for
 * them, and a lilac ring turns and breathes around them.
 */
export function Avatar({ assignee, size = 'sm', working = false }: { assignee: Who; size?: Size; working?: boolean }) {
  const agent = assignee.kind === 'Agent'
  const anonymous = assignee.name === ''
  const named = anonymous ? (agent ? 'An agent' : 'Someone') : agent ? `${assignee.name} (agent)` : assignee.name
  const title = working ? `${named} · an agent is working on this` : named
  return (
    <span className={`avatar avatar-${size}${agent ? ' agent' : ''}${anonymous ? ' anonymous' : ''}${working ? ' agent-at-work' : ''}`} title={title}>
      {agent ? (
        <SparkleIcon size={sparkleSize[size]} />
      ) : anonymous ? (
        <UserIcon size={sparkleSize[size]} />
      ) : (
        <Face name={assignee.name} avatarUrl={assignee.avatarUrl} />
      )}
    </span>
  )
}

interface AvatarStackProps {
  assignees: (Who & { principalId: string })[]
  max?: number
  size?: Size
  /** Whom an agent is working for right now: shown first, with its ring. */
  workingFor?: string | null
}

/** Overlapping avatars: the first `max` shown, then a count of the rest. */
export function AvatarStack({ assignees, max = 2, size = 'sm', workingFor }: AvatarStackProps) {
  const ordered = workingFor ? [...assignees.filter((a) => a.principalId === workingFor), ...assignees.filter((a) => a.principalId !== workingFor)] : assignees
  const shown = ordered.slice(0, max)
  const rest = assignees.length - shown.length
  return (
    <span className="avatar-stack">
      {/* Anonymous assignees share an empty id, so the place keeps them apart. */}
      {shown.map((a, i) => (
        <Avatar key={`${a.principalId}-${i}`} assignee={a} size={size} working={!!workingFor && a.principalId === workingFor} />
      ))}
      {rest > 0 && (
        <span className={`avatar avatar-${size} more`} title={ordered.slice(max).map((a) => a.name || 'Someone').join(', ')}>
          +{rest}
        </span>
      )}
    </span>
  )
}
