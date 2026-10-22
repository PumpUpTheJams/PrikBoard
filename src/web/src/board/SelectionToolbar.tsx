import type { Rect } from '../api/types'
import { keepPress } from './gesture'

/** How selected cards can be sorted into a grid. */
export type CardSort = 'number' | 'title' | 'tag' | 'color' | 'edited'

const sorts: { key: CardSort; label: string; title: string }[] = [
  { key: 'number', label: 'Number', title: 'By number: oldest card first' },
  { key: 'title', label: 'Title', title: 'By title, A to Z' },
  { key: 'tag', label: 'Tag', title: 'By first tag, A to Z; untagged last' },
  { key: 'color', label: 'Color', title: 'By color, in the order of the fill picker' },
  { key: 'edited', label: 'Last edited', title: 'Most recently edited first' },
]

interface SelectionToolbarProps {
  /** Around the selected cards, on the canvas. */
  rect: Rect
  /** The board's zoom: the toolbar keeps its size on screen. */
  zoom: number
  onSort: (sort: CardSort) => void
}

/** Above several selected cards: sorts them into a grid where they are. */
export function SelectionToolbar({ rect, zoom, onSort }: SelectionToolbarProps) {
  return (
    <div
      className="selection-toolbar"
      role="toolbar"
      aria-label="Selected cards"
      style={{ left: rect.x + rect.width / 2, top: rect.y - 12, transform: `translate(-50%, -100%) scale(${1 / zoom})` }}
      onPointerDown={keepPress}
    >
      <span className="selection-toolbar-label">Sort</span>
      {sorts.map((s) => (
        <button key={s.key} type="button" title={s.title} onClick={() => onSort(s.key)}>
          {s.label}
        </button>
      ))}
    </div>
  )
}
