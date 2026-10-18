import type { CSSProperties } from 'react'
import type { LiveViewer } from '../api/types'
import { SparkleIcon } from '../board/icons'
import { presenceColor } from '../live/presence'
import { initials } from './initials'

const MAX_SHOWN = 4

/** Who else is looking at the board right now, each ringed in the colour their drags show in. */
export function Presence({ viewers }: { viewers: LiveViewer[] }) {
  if (viewers.length === 0) return null
  const shown = viewers.slice(0, MAX_SHOWN)
  const rest = viewers.slice(MAX_SHOWN)
  const names = viewers.map((v) => v.displayName).join(', ')
  return (
    <div className="presence" role="group" aria-label={`Also here: ${names}`}>
      {shown.map((v) => (
        <span
          key={v.principalId}
          className={`avatar avatar-md presence-avatar${v.kind === 'Agent' ? ' agent' : ''}`}
          style={{ '--who': presenceColor(v.principalId) } as CSSProperties}
          title={`${v.displayName}${v.kind === 'Agent' ? ' (agent)' : ''} is here`}
        >
          {v.kind === 'Agent' ? <SparkleIcon size={13} /> : initials(v.displayName)}
        </span>
      ))}
      {rest.length > 0 && (
        <span className="avatar avatar-md more" title={rest.map((v) => v.displayName).join(', ')}>
          +{rest.length}
        </span>
      )}
    </div>
  )
}
