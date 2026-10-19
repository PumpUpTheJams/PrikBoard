import type { NoteBlockKind } from '../api/types'
import { ChecklistIcon, LinkIcon, NoteDocIcon } from './icons'

/** How each kind of block is named and drawn, in the bar and in its header. */
export const blockKinds: Record<NoteBlockKind, { label: string; Icon: typeof NoteDocIcon }> = {
  Text: { label: 'Note', Icon: NoteDocIcon },
  Checklist: { label: 'Checklist', Icon: ChecklistIcon },
  Links: { label: 'Links', Icon: LinkIcon },
}
