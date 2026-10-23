import type { BoardDetail, BoardSummary } from '../api/types'
import { ChatIcon, CheckIcon, ChevronDownIcon, DownloadIcon, RulesIcon, SignOutIcon, TrashIcon, UploadIcon } from '../board/icons'
import { trashPath } from '../board/links'
import { Menu, MenuItem } from './Menu'
import { QuickAdd } from './QuickAdd'

interface BoardMenuProps {
  boards: BoardSummary[] | null
  /** The open board, once it is in your list. */
  activeBoard: BoardSummary | null
  /** The open board's contents, once loaded. */
  board: BoardDetail | null
  /** Opens a board by its short id. */
  onSelect: (shortId: string) => void
  /** Creates a board and opens it; resolves false when that failed. */
  onCreate: (name: string) => Promise<boolean>
  onUpdate: (target: BoardSummary, changes: { name?: string; key?: string }) => Promise<boolean>
  onDialog: (dialog: 'rules' | 'discord') => void
  onExport: (target: BoardSummary) => void
  onImport: () => void
  /** These two ask first (ConfirmDialog). */
  onLeave: (target: BoardSummary) => void
  onDelete: (target: BoardSummary) => void
}

/** The board switcher in the top bar: your boards, a new one, and what can be done with the open one. */
export function BoardMenu(props: BoardMenuProps) {
  const { boards, activeBoard, board, onSelect, onCreate, onUpdate, onDialog, onExport, onImport, onLeave, onDelete } = props
  const isOwner = activeBoard?.yourRole === 'Owner'

  return (
    <Menu
      label="Boards"
      triggerClassName="board-switcher"
      trigger={
        <>
          <span className="board-name" title={activeBoard?.description ?? undefined}>
            {activeBoard?.name ?? 'PrikBoard'}
          </span>
          {activeBoard && (
            <span className="board-key" title={`Cards on this board are numbered ${activeBoard.key}-1, ${activeBoard.key}-2, …`}>
              {activeBoard.key}
            </span>
          )}
          <ChevronDownIcon />
        </>
      }
    >
      {(close) => (
        <>
          {boards && boards.length > 0 && <p className="menu-label">Boards</p>}
          {boards?.map((b) => (
            <MenuItem key={b.id} checked={b.id === activeBoard?.id} onClick={() => onSelect(b.shortId)}>
              <span className="menu-item-text">{b.name}</span>
              {b.id === activeBoard?.id && <CheckIcon />}
            </MenuItem>
          ))}
          <span className="menu-divider" role="separator" />
          <QuickAdd
            label="New board"
            placeholder="Project name"
            onAdd={async (name) => {
              const ok = await onCreate(name)
              if (ok) close()
              return ok
            }}
          />
          {activeBoard && isOwner && (
            <QuickAdd label="Rename board" placeholder={activeBoard.name} onAdd={(name) => onUpdate(activeBoard, { name })} />
          )}
          {activeBoard && isOwner && (
            // The key goes before card numbers (MPCT-415); addresses use the short id, so links keep working.
            <QuickAdd
              label="Change key"
              placeholder={`${activeBoard.key}, as in ${activeBoard.key}-1`}
              onAdd={(key) => onUpdate(activeBoard, { key })}
            />
          )}
          {board && (
            <MenuItem icon={<RulesIcon />} title="What this board is about and how agents should handle its cards" onClick={() => onDialog('rules')}>
              Agent rules
            </MenuItem>
          )}
          {activeBoard && isOwner && (
            <MenuItem icon={<ChatIcon />} title="Discord servers that preview this board's links" onClick={() => onDialog('discord')}>
              Discord
            </MenuItem>
          )}
          {board && (
            <MenuItem
              icon={<TrashIcon />}
              title="Cards deleted in the last 7 days"
              // The board opens its trash window from the address.
              onClick={() => {
                window.location.hash = trashPath(board.shortId).slice(1)
              }}
            >
              Deleted items
            </MenuItem>
          )}
          {activeBoard && (
            <MenuItem icon={<DownloadIcon />} onClick={() => onExport(activeBoard)}>
              Export board
            </MenuItem>
          )}
          <MenuItem icon={<UploadIcon />} onClick={onImport}>
            Import board
          </MenuItem>
          {activeBoard && (
            <MenuItem icon={<SignOutIcon />} onClick={() => onLeave(activeBoard)}>
              Leave board
            </MenuItem>
          )}
          {activeBoard && isOwner && (
            <MenuItem icon={<TrashIcon />} danger onClick={() => onDelete(activeBoard)}>
              Delete board
            </MenuItem>
          )}
        </>
      )}
    </Menu>
  )
}
