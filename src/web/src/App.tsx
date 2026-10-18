import { useEffect, useState } from 'react'
import { api, errorMessage } from './api/client'
import type { BoardSummary } from './api/types'
import { BoardView } from './components/BoardView'
import { QuickAdd } from './components/QuickAdd'

const boardIdFromHash = () => window.location.hash.match(/^#\/boards\/([\w-]+)/)?.[1] ?? null

export default function App() {
  const [boards, setBoards] = useState<BoardSummary[] | null>(null)
  const [selectedId, setSelectedId] = useState(boardIdFromHash)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.listBoards().then(setBoards, (e) => setError(errorMessage(e)))

    const onHashChange = () => setSelectedId(boardIdFromHash())
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  // Fall back to the newest board when nothing (or something stale) is selected.
  const activeBoard = boards?.find((b) => b.id === selectedId) ?? boards?.[0] ?? null

  function selectBoard(id: string | null) {
    window.location.hash = id ? `/boards/${id}` : ''
    setSelectedId(id)
  }

  async function createBoard(name: string) {
    try {
      const board = await api.createBoard(name)
      setBoards((current) => [board, ...(current ?? [])])
      selectBoard(board.id)
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function deleteBoard(board: BoardSummary) {
    if (!window.confirm(`Delete "${board.name}" and all of its notes?`)) return
    try {
      await api.deleteBoard(board.id)
      setBoards((current) => current?.filter((b) => b.id !== board.id) ?? null)
      selectBoard(null)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div className="app">
      <header className="topbar">
        <h1 className="brand">
          <span className="brand-mark" aria-hidden="true" />
          PrikBoard
        </h1>
        {activeBoard && (
          <select aria-label="Board" value={activeBoard.id} onChange={(e) => selectBoard(e.target.value)}>
            {boards!.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </select>
        )}
        <QuickAdd label="New board" placeholder="Project name" onAdd={createBoard} />
        <div className="spacer" />
        {activeBoard && (
          <button type="button" className="danger" onClick={() => void deleteBoard(activeBoard)}>
            Delete board
          </button>
        )}
        <a className="api-link" href="/openapi/v1.json" target="_blank" rel="noreferrer">
          API
        </a>
      </header>

      {error && (
        <div className="error" role="alert">
          <span>{error}</span>
          <button type="button" className="ghost" onClick={() => setError(null)}>
            Dismiss
          </button>
        </div>
      )}

      <main>
        {boards === null ? (
          <p className="status">Loading…</p>
        ) : activeBoard ? (
          <BoardView key={activeBoard.id} boardId={activeBoard.id} onError={setError} />
        ) : (
          <div className="empty">
            <h2>No boards yet</h2>
            <p>Create a board for your project, then start sticking notes on it.</p>
          </div>
        )}
      </main>
    </div>
  )
}
