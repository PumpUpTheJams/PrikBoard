import { useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardDetail, ColumnDetail, NoteDetail } from '../api/types'
import { NoteCard } from './NoteCard'
import { QuickAdd } from './QuickAdd'

interface BoardViewProps {
  boardId: string
  onError: (message: string) => void
}

export function BoardView({ boardId, onError }: BoardViewProps) {
  const [board, setBoard] = useState<BoardDetail | null>(null)
  const [dragging, setDragging] = useState<NoteDetail | null>(null)
  const [dropColumnId, setDropColumnId] = useState<string | null>(null)

  useEffect(() => {
    let ignore = false
    api.getBoard(boardId).then(
      (loaded) => !ignore && setBoard(loaded),
      (error) => onError(errorMessage(error)),
    )
    return () => {
      ignore = true
    }
  }, [boardId, onError])

  /** Runs a mutation, then reloads the board so the UI always shows the server's ordering. */
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    let ok = true
    try {
      await action()
    } catch (error) {
      onError(errorMessage(error))
      ok = false
    }
    try {
      setBoard(await api.getBoard(boardId))
    } catch (error) {
      onError(errorMessage(error))
    }
    return ok
  }

  function endDrag() {
    setDragging(null)
    setDropColumnId(null)
  }

  function dropNote(column: ColumnDetail, before?: NoteDetail) {
    const note = dragging
    endDrag()
    if (!note || note.id === before?.id) return

    // The API removes the note before inserting it, so moving down within a column shifts the target up by one.
    let position = before?.position
    if (before && note.columnId === column.id && note.position < before.position) position = before.position - 1

    void run(() => api.moveNote(note.id, column.id, position))
  }

  if (!board) return <p className="status">Loading board…</p>

  return (
    <section className="board">
      {board.description && <p className="board-description">{board.description}</p>}
      <div className="columns">
        {board.columns.map((column) => (
          <section
            key={column.id}
            className={`column${dropColumnId === column.id ? ' drop-target' : ''}`}
            onDragOver={(e) => {
              if (!dragging) return
              e.preventDefault()
              setDropColumnId(column.id)
            }}
            onDragLeave={(e) => {
              if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDropColumnId(null)
            }}
            onDrop={(e) => {
              e.preventDefault()
              dropNote(column)
            }}
          >
            <header className="column-header">
              <h2>{column.name}</h2>
              <span className="count">{column.notes.length}</span>
              {column.notes.length === 0 && (
                <button
                  type="button"
                  className="ghost icon"
                  title="Delete column"
                  aria-label={`Delete column ${column.name}`}
                  onClick={() => void run(() => api.deleteColumn(column.id))}
                >
                  ×
                </button>
              )}
            </header>
            <div className="notes">
              {column.notes.map((note) => (
                <NoteCard
                  key={note.id}
                  note={note}
                  onSave={(changes) => run(() => api.updateNote(note.id, changes))}
                  onDelete={() => void run(() => api.deleteNote(note.id))}
                  onDragStart={() => setDragging(note)}
                  onDragEnd={endDrag}
                  onDropBefore={() => dropNote(column, note)}
                />
              ))}
            </div>
            <QuickAdd
              label="Add note"
              placeholder="What needs doing?"
              onAdd={(title) => run(() => api.createNote(board.id, { title, columnId: column.id }))}
            />
          </section>
        ))}
        <div className="column column-new">
          <QuickAdd
            label="Add column"
            placeholder="Column name"
            onAdd={(name) => run(() => api.createColumn(board.id, name))}
          />
        </div>
      </div>
    </section>
  )
}
