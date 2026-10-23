import type { CSSProperties } from 'react'
import type { LiveViewer } from '../api/types'
import { SparkleIcon, UserIcon } from '../board/icons'
import { presenceColor } from '../live/presence'
import { Face } from './Avatar'

const MAX_SHOWN = 4

/**
 * Who else is looking at the board right now, each ringed in the color their drags show in, and how many guests
 * (visitors of a public board who aren't members) are looking too. `anonymous`: a visitor of a board that hides its
 * people, for whom `guests` is everyone else looking.
 */
export function Presence({ viewers, guests = 0, anonymous = false }: { viewers: LiveViewer[]; guests?: number; anonymous?: boolean }) {
  if (viewers.length === 0 && guests === 0) return null
  const shown = viewers.slice(0, MAX_SHOWN)
  const rest = viewers.slice(MAX_SHOWN)
  const guestsLabel = anonymous ? (guests === 1 ? '1 other' : `${guests} others`) : guests === 1 ? '1 guest' : `${guests} guests`
  const names = [...viewers.map((v) => v.displayName), ...(guests > 0 ? [guestsLabel] : [])].join(', ')
  return (
    <div className="presence" role="group" aria-label={`Also here: ${names}`}>
      {guests > 0 && (
        <span className="presence-guests" title={`${guestsLabel} looking at this board`}>
          <UserIcon size={13} />
          {guests}
        </span>
      )}
      {shown.map((v) => (
        <span
          key={v.principalId}
          className={`avatar avatar-md presence-avatar${v.kind === 'Agent' ? ' agent' : ''}`}
          style={{ '--who': presenceColor(v.principalId) } as CSSProperties}
          title={`${v.displayName}${v.kind === 'Agent' ? ' (agent)' : ''} is here`}
        >
          {v.kind === 'Agent' ? <SparkleIcon size={13} /> : <Face name={v.displayName} avatarUrl={v.avatarUrl} />}
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
