import * as vscode from 'vscode'
import { api, errorMessage } from '../../web/src/api/client'
import type {
  Assignee,
  BoardDetail,
  BoardSummary,
  BucketColor,
  BucketDetail,
  ChecklistItem,
  NoteColor,
  NoteDetail,
  NoteLink,
} from '../../web/src/api/types'
import { webUrl } from './auth'

export type Node =
  | { kind: 'board'; board: BoardSummary }
  /** `bucket` is null for notes that sit in no bucket. */
  | { kind: 'bucket'; bucket: BucketDetail | null; notes: NoteDetail[] }
  | { kind: 'note'; note: NoteDetail }
  | { kind: 'checklist'; item: ChecklistItem }
  | { kind: 'link'; link: NoteLink }
  | { kind: 'assignee'; assignee: Assignee }

const noteColors: Record<NoteColor, string> = {
  Butter: 'charts.yellow',
  Mint: 'charts.green',
  Sky: 'charts.blue',
  Coral: 'charts.red',
  Lilac: 'charts.purple',
}

const bucketColors: Record<BucketColor, string> = {
  Blue: 'charts.blue',
  Amber: 'charts.orange',
  Violet: 'charts.purple',
  Teal: 'charts.green',
  Rose: 'charts.red',
}

const icon = (id: string, color?: string) => new vscode.ThemeIcon(id, color ? new vscode.ThemeColor(color) : undefined)

/** Boards, then their buckets, notes and a note's checklist, links and assignees. A board loads when it is first opened. */
export class BoardTree implements vscode.TreeDataProvider<Node>, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<Node | undefined>()
  readonly onDidChangeTreeData = this.changed.event

  private boards: BoardSummary[] | undefined
  private readonly details = new Map<string, BoardDetail>()
  private failed = false

  /** Forgets everything loaded; the open rows load again. */
  refresh() {
    this.boards = undefined
    this.details.clear()
    this.failed = false
    this.changed.fire(undefined)
  }

  async getChildren(node?: Node): Promise<Node[]> {
    try {
      if (!node) return (await this.loadBoards()).map((board): Node => ({ kind: 'board', board }))
      switch (node.kind) {
        case 'board':
          return this.bucketsOf(await this.loadBoard(node.board.id))
        case 'bucket':
          return node.notes.map((note): Node => ({ kind: 'note', note }))
        case 'note':
          return [
            ...node.note.checklist.map((item): Node => ({ kind: 'checklist', item })),
            ...node.note.links.map((link): Node => ({ kind: 'link', link })),
            ...node.note.assignees.map((assignee): Node => ({ kind: 'assignee', assignee })),
          ]
        default:
          return []
      }
    } catch (error) {
      // A rejected token is handled by the client's unauthorized handler; anything else is said once until the next refresh.
      if (!this.failed) void vscode.window.showErrorMessage(`PrikBoard: ${errorMessage(error)}`)
      this.failed = true
      return []
    }
  }

  getTreeItem(node: Node): vscode.TreeItem {
    switch (node.kind) {
      case 'board': {
        const item = new vscode.TreeItem(node.board.name, vscode.TreeItemCollapsibleState.Collapsed)
        item.contextValue = 'board'
        item.iconPath = icon('project')
        item.description = node.board.yourRole === 'Viewer' ? 'view only' : undefined
        item.tooltip = node.board.description ?? node.board.name
        return item
      }
      case 'bucket': {
        const item = new vscode.TreeItem(node.bucket?.name ?? 'No bucket', vscode.TreeItemCollapsibleState.Expanded)
        item.contextValue = 'bucket'
        item.iconPath = icon('circle-filled', node.bucket ? bucketColors[node.bucket.color] : undefined)
        item.description = String(node.notes.length)
        return item
      }
      case 'note':
        return this.noteItem(node.note)
      case 'checklist': {
        const item = new vscode.TreeItem(node.item.text)
        item.contextValue = 'checklist'
        item.iconPath = icon(node.item.done ? 'pass' : 'circle-large-outline')
        return item
      }
      case 'link': {
        const item = new vscode.TreeItem(node.link.title || node.link.url)
        item.contextValue = 'link'
        item.iconPath = icon('link')
        item.tooltip = node.link.url
        item.command = { command: 'vscode.open', title: 'Open link', arguments: [vscode.Uri.parse(node.link.url)] }
        return item
      }
      case 'assignee': {
        const item = new vscode.TreeItem(node.assignee.name)
        item.contextValue = 'assignee'
        item.iconPath = icon(node.assignee.kind === 'Agent' ? 'sparkle' : 'person')
        return item
      }
    }
  }

  /** The web app address of a board or note row, for "Open in PrikBoard". */
  urlOf(node: Node): string | undefined {
    const boardId = node.kind === 'board' ? node.board.id : node.kind === 'note' ? node.note.boardId : undefined
    return boardId ? `${webUrl()}/#/boards/${boardId}` : undefined
  }

  private noteItem(note: NoteDetail) {
    const hasDetails = note.checklist.length > 0 || note.links.length > 0 || note.assignees.length > 0
    const item = new vscode.TreeItem(
      note.title,
      hasDetails ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None,
    )
    item.contextValue = 'note'
    item.iconPath = note.agentWorking ? icon('loading~spin') : icon('note', noteColors[note.color])

    const done = note.checklist.filter((entry) => entry.done).length
    item.description = [...note.tags.map((tag) => `#${tag}`), note.checklist.length > 0 ? `${done}/${note.checklist.length}` : '']
      .filter(Boolean)
      .join(' ')

    // appendText escapes markdown, so a note cannot format or link its own tooltip.
    const tooltip = new vscode.MarkdownString()
    tooltip.appendText(note.title)
    if (note.body) tooltip.appendText(`\n\n${note.body}`)
    item.tooltip = tooltip
    return item
  }

  private bucketsOf(board: BoardDetail): Node[] {
    const buckets = board.buckets.map((bucket): Node => ({
      kind: 'bucket',
      bucket,
      notes: board.notes.filter((note) => note.bucketId === bucket.id),
    }))
    const loose = board.notes.filter((note) => note.bucketId === null)
    return loose.length > 0 ? [...buckets, { kind: 'bucket', bucket: null, notes: loose }] : buckets
  }

  private async loadBoards() {
    this.boards ??= await api.listBoards()
    return this.boards
  }

  private async loadBoard(boardId: string) {
    let board = this.details.get(boardId)
    if (!board) {
      board = await api.getBoard(boardId)
      this.details.set(boardId, board)
    }
    return board
  }

  dispose() {
    this.changed.dispose()
  }
}
