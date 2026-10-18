import type { CSSProperties, PointerEvent } from 'react'
import type { NoteDetail, Point } from '../api/types'
import { AvatarStack } from '../components/Avatar'
import { ExpandIcon } from './icons'
import { NOTE_HEIGHT, NOTE_WIDTH } from './layout'

/** Rough width of a tag chip: 11 px mono is about 6.6 px per character, plus padding and gap. */
const tagWidth = (tag: string) => tag.length * 6.6 + 16

/** Title lines that fit: three, or two when the tags wrap to a second row. */
function titleLines(note: NoteDetail) {
  const avatars = Math.min(note.assignees.length, 3)
  const room = NOTE_WIDTH - 24 - (avatars > 0 ? 24 + (avatars - 1) * 18 + 8 : 0)
  return note.tags.reduce((sum, tag) => sum + tagWidth(tag), 0) > room ? 2 : 3
}

interface PostItProps {
  note: NoteDetail
  /** Where to draw it: the server position, or the live position while dragging. */
  point: Point
  selected: boolean
  lifted: boolean
  onPointerDown: (e: PointerEvent<HTMLDivElement>) => void
  onExpand: () => void
}

/** On the board a post-it shows its title, tags and assignees. Everything else lives inside (see ExpandedNote). */
export function PostIt({ note, point, selected, lifted, onPointerDown, onExpand }: PostItProps) {
  const classes = ['post-it', `fill-${note.color.toLowerCase()}`]
  if (selected) classes.push('selected')
  if (lifted) classes.push('lifted')
  if (note.agentWorking) classes.push('agent-working')

  return (
    <div
      className={classes.join(' ')}
      style={{ left: point.x, top: point.y, width: NOTE_WIDTH, height: NOTE_HEIGHT, '--title-lines': titleLines(note) } as CSSProperties}
      role="group"
      aria-label={note.agentWorking ? `${note.title} (agent working)` : note.title}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onDoubleClick={onExpand}
      onKeyDown={(e) => e.key === 'Enter' && e.target === e.currentTarget && onExpand()}
    >
      <div className="post-it-head">
        <span className="post-it-title">{note.title}</span>
        <button
          type="button"
          className="post-it-expand"
          aria-label={`Expand ${note.title}`}
          onPointerDown={(e) => e.stopPropagation()}
          onClick={onExpand}
        >
          <ExpandIcon />
        </button>
      </div>
      <div className="post-it-foot">
        <span className="post-it-tags">
          {note.tags.map((tag) => (
            <span key={tag} className="tag">
              {tag}
            </span>
          ))}
        </span>
        {note.assignees.length > 0 ? <AvatarStack assignees={note.assignees} /> : <span className="avatar-slot" aria-hidden="true" />}
      </div>
      {selected && (
        <>
          <span className="handle nw" aria-hidden="true" />
          <span className="handle ne" aria-hidden="true" />
          <span className="handle sw" aria-hidden="true" />
          <span className="handle se" aria-hidden="true" />
        </>
      )}
    </div>
  )
}
