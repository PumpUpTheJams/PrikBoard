import type { CSSProperties } from 'react'
import type { LiveViewer } from '../api/types'
import { SparkleIcon, UserIcon } from '../board/icons'
import { presenceColor } from '../live/presence'
import { Face } from './Avatar'
import { plural } from './plural'

const MAX_SHOWN = 4

/**
 * Who else is looking at the board right now, each ringed in the color their drags, pointer and pins show in, and how many
 * guests (visitors of a public board who aren't members) are looking too. `anonymous`: a visitor of a board that hides
 * its people, for whom `guests` is everyone else looking. `pickable`: those whose pointer is on the board; picking one
 * (`onPick`) goes to their pointer (or pins).
 */
export function Presence({ viewers, guests = 0, anonymous = false, pickable, onPick }: {
  viewers: LiveViewer[]
  guests?: number
  anonymous?: boolean
  pickable?: Set<string>
  onPick?: (principalId: string) => void
}) {
  if (viewers.length === 0 && guests === 0) return null
  const shown = viewers.slice(0, MAX_SHOWN)
  const rest = viewers.slice(MAX_SHOWN)
  const guestsLabel = anonymous ? plural(guests, 'other', 'others') : plural(guests, 'guest', 'guests')
  const names = [...viewers.map((v) => v.displayName), ...(guests > 0 ? [guestsLabel] : [])].join(', ')
  return (
    <div className="presence" role="group" aria-label={`Also here: ${names}`}>
      {guests > 0 && (
        <span className="presence-guests" title={`${guestsLabel} looking at this board`}>
          <UserIcon size={13} />
          {guests}
        </span>
      )}
      {shown.map((v) => {
        const className = `avatar avatar-md presence-avatar${v.kind === 'Agent' ? ' agent' : ''}`
        const style = { '--who': presenceColor(v.principalId) } as CSSProperties
        const name = `${v.displayName}${v.kind === 'Agent' ? ' (agent)' : ''}`
        const face = v.kind === 'Agent' ? <SparkleIcon size={13} /> : <Face name={v.displayName} avatarUrl={v.avatarUrl} />
        return onPick && pickable?.has(v.principalId) ? (
          <button key={v.principalId} type="button" className={`${className} pickable`} style={style} title={`Show where ${name} is`} onClick={() => onPick(v.principalId)}>
            {face}
          </button>
        ) : (
          <span key={v.principalId} className={className} style={style} title={`${name} is here`}>
            {face}
          </span>
        )
      })}
      {rest.length > 0 && (
        <span className="avatar avatar-md more" title={rest.map((v) => v.displayName).join(', ')}>
          +{rest.length}
        </span>
      )}
    </div>
  )
}
