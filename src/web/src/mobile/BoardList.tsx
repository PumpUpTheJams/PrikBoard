import { useState, type ReactNode } from 'react'
import type { BoardSummary } from '../api/types'
import { CardIcon, ChevronRightIcon, SearchIcon } from '../board/icons'
import { fold } from '../board/search'
import { QuickAdd } from '../components/QuickAdd'
import { navigate } from './nav'

interface BoardListProps {
  boards: BoardSummary[]
  onCreate: (name: string) => Promise<boolean>
  /** The settings menu, at the end of the top bar. */
  settings: ReactNode
}

/** The first mobile screen: your boards, to pick one. */
export function BoardList({ boards, onCreate, settings }: BoardListProps) {
  const [query, setQuery] = useState('')
  const words = fold(query).split(/\s+/).filter(Boolean)
  const shown = boards.filter((b) => words.every((w) => fold(`${b.name} ${b.key} ${b.description ?? ''}`).includes(w)))

  return (
    <div className="m-screen">
      <header className="m-bar">
        <span className="board-tile" aria-hidden="true">
          <CardIcon size={16} />
        </span>
        <div className="m-bar-title">
          <span className="m-bar-name">PrikBoard</span>
        </div>
        {settings}
      </header>

      <div className="m-scroll">
        <div className="m-heading">
          <h1>
            Boards <span className="m-count">{boards.length}</span>
          </h1>
          <QuickAdd label="New board" placeholder="Project name" onAdd={onCreate} />
        </div>

        {boards.length > 3 && (
          <label className="m-search">
            <SearchIcon />
            <input type="search" aria-label="Search boards" placeholder="Search boards" value={query} onChange={(e) => setQuery(e.target.value)} />
          </label>
        )}

        <ul className="m-list">
          {shown.map((b) => (
            <li key={b.id}>
              <button type="button" className="m-row" onClick={() => navigate(`/#/boards/${b.shortId}`)}>
                <span className="m-row-text">
                  <span className="m-row-title">
                    <span className="m-row-name">{b.name}</span>
                    <span className="board-key">{b.key}</span>
                  </span>
                  <span className="m-row-detail">{b.description ?? (b.yourRole === 'Viewer' ? 'You can look, not change' : `You are ${b.yourRole.toLowerCase()}`)}</span>
                </span>
                <ChevronRightIcon />
              </button>
            </li>
          ))}
        </ul>
        {boards.length === 0 && <p className="m-empty">No boards yet. Create one for your project with New board.</p>}
        {boards.length > 0 && shown.length === 0 && <p className="m-empty">No board matches “{query}”.</p>}
        <p className="m-note">Arranging buckets and the canvas needs a larger screen.</p>
      </div>
    </div>
  )
}
