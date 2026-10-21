import type { CardComponentKind } from '../api/types'
import { ChecklistIcon, LinkIcon, TextIcon } from './icons'

/** How each kind of component is named and drawn, in the bar and in its header. */
export const componentKinds: Record<CardComponentKind, { label: string; Icon: typeof TextIcon }> = {
  Text: { label: 'Text', Icon: TextIcon },
  Checklist: { label: 'Checklist', Icon: ChecklistIcon },
  Links: { label: 'Links', Icon: LinkIcon },
}
