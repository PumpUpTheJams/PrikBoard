import { useEffect, useState } from 'react'
import { api, errorMessage, setUnauthorizedHandler } from './api/client'
import type { Assignee, BoardSummary, Caller } from './api/types'
import { BoardCanvas } from './board/BoardCanvas'
import { CheckIcon, ChevronDownIcon, ExternalIcon, MoonIcon, PostItIcon, SignOutIcon, SunIcon, TrashIcon } from './board/icons'
import { useBoard } from './board/useBoard'
import { AvatarStack } from './components/Avatar'
import { ConfirmDialog } from './components/ConfirmDialog'
import { initials } from './components/initials'
import { Menu } from './components/Menu'
import { QuickAdd } from './components/QuickAdd'
import { SignIn } from './components/SignIn'

const boardIdFromHash = () => window.location.hash.match(/^#\/boards\/([\w-]+)/)?.[1] ?? null

type Theme = 'dark' | 'light'
const themeKey = 'prikboard-theme'

/** Dark ("night shift") unless the viewer switched to light before. */
function storedTheme(): Theme {
  try {
    return localStorage.getItem(themeKey) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

/** Everyone assigned to something on the board, people first. */
function boardAssignees(notes: { assignees: Assignee[] }[]): Assignee[] {
  const unique = new Map<string, Assignee>()
  for (const a of notes.flatMap((n) => n.assignees)) unique.set(`${a.kind}:${a.name.toLowerCase()}`, a)
  return [...unique.values()].sort((a, b) => (a.kind === b.kind ? 0 : a.kind === 'Person' ? -1 : 1))
}

export default function App() {
  const [theme, setTheme] = useState(storedTheme)
  // undefined while checking the session, null when signed out.
  const [user, setUser] = useState<Caller | null | undefined>(undefined)

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    try {
      localStorage.setItem(themeKey, theme)
    } catch {
      // Storage can be unavailable (private mode); the theme then lasts for this visit only.
    }
  }, [theme])

  useEffect(() => {
    // Any 401, such as an expired session, drops back to the sign-in screen.
    setUnauthorizedHandler(() => setUser(null))
    api.whoAmI().then(setUser, () => setUser(null))
  }, [])

  async function signOut() {
    await api.logout().catch(() => {})
    setUser(null)
  }

  if (user === undefined) return <p className="status">Loading…</p>
  if (user === null) return <SignIn />
  return <Workspace user={user} theme={theme} onThemeChange={setTheme} onSignOut={() => void signOut()} />
}

interface WorkspaceProps {
  user: Caller
  theme: Theme
  onThemeChange: (theme: Theme) => void
  onSignOut: () => void
}

function Workspace({ user, theme, onThemeChange, onSignOut }: WorkspaceProps) {
  const [boards, setBoards] = useState<BoardSummary[] | null>(null)
  const [selectedId, setSelectedId] = useState(boardIdFromHash)
  const [error, setError] = useState<string | null>(null)
  const [boardToDelete, setBoardToDelete] = useState<BoardSummary | null>(null)

  useEffect(() => {
    api.listBoards().then(setBoards, (e) => setError(errorMessage(e)))

    const onHashChange = () => setSelectedId(boardIdFromHash())
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  // Fall back to the newest board when nothing (or something stale) is selected.
  const activeBoard = boards?.find((b) => b.id === selectedId) ?? boards?.[0] ?? null
  const { board, run, setPaused } = useBoard(activeBoard?.id ?? null, setError)
  const assignees = board ? boardAssignees(board.notes) : []

  function selectBoard(id: string | null) {
    window.location.hash = id ? `/boards/${id}` : ''
    setSelectedId(id)
  }

  async function createBoard(name: string) {
    try {
      const created = await api.createBoard(name)
      setBoards((current) => [created, ...(current ?? [])])
      selectBoard(created.id)
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function renameBoard(target: BoardSummary, name: string) {
    try {
      const renamed = await api.updateBoard(target.id, { name })
      setBoards((current) => current?.map((b) => (b.id === renamed.id ? renamed : b)) ?? null)
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    }
  }

  async function deleteBoard(target: BoardSummary) {
    setBoardToDelete(null)
    try {
      await api.deleteBoard(target.id)
      setBoards((current) => current?.filter((b) => b.id !== target.id) ?? null)
      selectBoard(null)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-start">
          <span className="board-tile" aria-hidden="true">
            <PostItIcon size={16} />
          </span>
          <h1 className="visually-hidden">{activeBoard?.name ?? 'PrikBoard'}</h1>
          <Menu
            label="Boards"
            triggerClassName="board-switcher"
            trigger={
              <>
                <span className="board-name" title={activeBoard?.description ?? undefined}>
                  {activeBoard?.name ?? 'PrikBoard'}
                </span>
                <ChevronDownIcon />
              </>
            }
          >
            {(close) => (
              <>
                {boards && boards.length > 0 && <p className="menu-label">Boards</p>}
                {boards?.map((b) => (
                  <button
                    key={b.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={b.id === activeBoard?.id}
                    className="menu-item"
                    onClick={() => {
                      selectBoard(b.id)
                      close()
                    }}
                  >
                    <span className="menu-item-text">{b.name}</span>
                    {b.id === activeBoard?.id && <CheckIcon />}
                  </button>
                ))}
                <span className="menu-divider" role="separator" />
                <QuickAdd
                  label="New board"
                  placeholder="Project name"
                  onAdd={async (name) => {
                    const ok = await createBoard(name)
                    if (ok) close()
                    return ok
                  }}
                />
                {activeBoard && (
                  <>
                    <QuickAdd label="Rename board" placeholder={activeBoard.name} onAdd={(name) => renameBoard(activeBoard, name)} />
                    <button
                      type="button"
                      role="menuitem"
                      className="menu-item danger"
                      onClick={() => {
                        close()
                        setBoardToDelete(activeBoard)
                      }}
                    >
                      <TrashIcon />
                      Delete board
                    </button>
                  </>
                )}
              </>
            )}
          </Menu>
        </div>

        <div className="topbar-end">
          {assignees.length > 0 && (
            <div className="topbar-people" aria-label="Assigned on this board">
              <AvatarStack assignees={assignees} max={3} size="md" />
            </div>
          )}
          <span className="topbar-divider" aria-hidden="true" />
          <Menu
            label="Your settings"
            align="end"
            triggerClassName="settings-trigger"
            trigger={
              <>
                <span className="me">{initials(user.name)}</span>
                <ChevronDownIcon />
              </>
            }
          >
            {(close) => (
              <>
                <div className="menu-user">
                  <span className="me">{initials(user.name)}</span>
                  <span className="menu-user-text">
                    <span className="menu-user-name">{user.name}</span>
                    <span className="menu-user-detail">{user.email ?? `Signed in with ${user.provider}`}</span>
                  </span>
                </div>
                <span className="menu-divider" role="separator" />
                <button
                  type="button"
                  role="menuitem"
                  className="menu-item"
                  onClick={() => onThemeChange(theme === 'dark' ? 'light' : 'dark')}
                >
                  {theme === 'dark' ? <SunIcon /> : <MoonIcon />}
                  {theme === 'dark' ? 'Light theme' : 'Night shift theme'}
                </button>
                <a role="menuitem" className="menu-item" href="/scalar" target="_blank" rel="noreferrer" onClick={close}>
                  <ExternalIcon />
                  API reference
                </a>
                <a role="menuitem" className="menu-item" href="/openapi/v1.json" target="_blank" rel="noreferrer" onClick={close}>
                  <ExternalIcon />
                  OpenAPI document
                </a>
                <span className="menu-divider" role="separator" />
                <button type="button" role="menuitem" className="menu-item" onClick={onSignOut}>
                  <SignOutIcon />
                  Sign out
                </button>
              </>
            )}
          </Menu>
        </div>
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
          board ? (
            <BoardCanvas key={activeBoard.id} board={board} run={run} setPaused={setPaused} />
          ) : (
            <p className="status">Loading board…</p>
          )
        ) : (
          <div className="empty">
            <span className="board-tile large" aria-hidden="true">
              <PostItIcon size={28} />
            </span>
            <h2>No boards yet</h2>
            <p>Create a board for your project, then start sticking post-its on it.</p>
            <QuickAdd label="Create a board" placeholder="Project name" onAdd={createBoard} />
          </div>
        )}
      </main>

      {boardToDelete && (
        <ConfirmDialog
          title="Delete this board?"
          message={`"${boardToDelete.name}" and all of its buckets and post-its will be deleted. This can't be undone.`}
          confirmLabel="Delete board"
          onConfirm={() => void deleteBoard(boardToDelete)}
          onCancel={() => setBoardToDelete(null)}
        />
      )}
    </div>
  )
}
