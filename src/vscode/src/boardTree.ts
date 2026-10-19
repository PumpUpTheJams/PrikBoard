import * as vscode from 'vscode'
import { api, errorMessage } from '../../web/src/api/client'
import type {
  Assignee,
  BoardDetail,
  BoardSummary,
  BucketColor,
  BucketDetail,
  Caller,
  NoteBlock,
  NoteColor,
  NoteDetail,
  NoteLink,
} from '../../web/src/api/types'
import { webUrl } from './auth'

/**
 * `canEdit` is false on boards where the caller is a Viewer; it decides which commands a row offers.
 */
export type Node =
  /** The notes assigned to the signed-in caller, across all their boards. */
  | { kind: 'mine'; me: Caller }
  | { kind: 'board'; board: BoardSummary }
  /** `bucket` is null for notes that sit in no bucket. */
  | { kind: 'bucket'; boardId: string; bucket: BucketDetail | null; notes: NoteDetail[]; canEdit: boolean }
  /** `where` (board and bucket) is set where a note is shown outside its board, under "Assigned to you". */
  | { kind: 'note'; note: NoteDetail; canEdit: boolean; where?: string }
  /** One item of one of the note's checklist blocks. */
  | { kind: 'checklist'; note: NoteDetail; block: NoteBlock; index: number; canEdit: boolean }
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

/** A note's content is blocks; the tree shows the items of all its checklists and all its links. */
const checklistsOf = (note: NoteDetail) => note.blocks.filter((b) => b.kind === 'Checklist')
const itemsOf = (note: NoteDetail) => checklistsOf(note).flatMap((b) => b.items ?? [])
const linksOf = (note: NoteDetail) => note.blocks.flatMap((b) => b.links ?? [])

const icon = (id: string, color?: string) => new vscode.ThemeIcon(id, color ? new vscode.ThemeColor(color) : undefined)

/** Menus in package.json match these, for example `viewItem == note.edit`. */
const contextOf = (kind: string, canEdit = false) => (canEdit ? `${kind}.edit` : kind)

/**
 * "Assigned to you", then boards with their buckets, notes and a note's checklist, links and assignees. A board loads
 * when it is first opened; "Assigned to you" loads all of them.
 */
export class BoardTree implements vscode.TreeDataProvider<Node>, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<Node | undefined>()
  readonly onDidChangeTreeData = this.changed.event

  private boards: BoardSummary[] | undefined
  private readonly details = new Map<string, BoardDetail>()
  private failed = false

  /** Who is signed in (`whoAmI`), or undefined until it has loaded; assignees are matched on its id. */
  private readonly me: () => Caller | undefined

  constructor(me: () => Caller | undefined) {
    this.me = me
  }

  /** Forgets everything loaded; the open rows load again. */
  refresh() {
    this.boards = undefined
    this.details.clear()
    this.failed = false
    this.changed.fire(undefined)
  }

  /** The buckets of a board that is open in the tree, in the order the board draws them. */
  bucketsOf(boardId: string): BucketDetail[] {
    return this.details.get(boardId)?.buckets ?? []
  }

  async getChildren(node?: Node): Promise<Node[]> {
    try {
      if (!node) {
        const me = this.me()
        const boards = (await this.loadBoards()).map((board): Node => ({ kind: 'board', board }))
        return me ? [{ kind: 'mine', me }, ...boards] : boards
      }
      switch (node.kind) {
        case 'mine':
          return this.assignedTo(node.me)
        case 'board':
          return this.bucketNodes(await this.loadBoard(node.board.id))
        case 'bucket':
          return node.notes.map((note): Node => ({ kind: 'note', note, canEdit: node.canEdit }))
        case 'note':
          return [
            ...checklistsOf(node.note).flatMap((block) =>
              (block.items ?? []).map((_, index): Node => ({ kind: 'checklist', note: node.note, block, index, canEdit: node.canEdit })),
            ),
            ...linksOf(node.note).map((link): Node => ({ kind: 'link', link })),
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
      case 'mine': {
        const item = new vscode.TreeItem('Assigned to you', vscode.TreeItemCollapsibleState.Expanded)
        item.contextValue = contextOf('mine')
        item.iconPath = icon('account')
        item.description = node.me.displayName
        item.tooltip = `Notes assigned to ${node.me.displayName}, on all your boards`
        return item
      }
      case 'board': {
        const item = new vscode.TreeItem(node.board.name, vscode.TreeItemCollapsibleState.Collapsed)
        item.contextValue = contextOf('board')
        item.iconPath = icon('project')
        // Always shown: it decides whether the edit commands appear.
        item.description = node.board.yourRole === 'Viewer' ? 'view only' : node.board.yourRole
        item.tooltip = node.board.description ?? node.board.name
        return item
      }
      case 'bucket': {
        const item = new vscode.TreeItem(node.bucket?.name ?? 'No bucket', vscode.TreeItemCollapsibleState.Expanded)
        item.contextValue = contextOf('bucket', node.canEdit)
        item.iconPath = icon('circle-filled', node.bucket ? bucketColors[node.bucket.color] : undefined)
        item.description = String(node.notes.length)
        return item
      }
      case 'note':
        return this.noteItem(node.note, node.canEdit, node.where)
      case 'checklist': {
        const entry = node.block.items![node.index]
        const item = new vscode.TreeItem(entry.text)
        item.contextValue = contextOf('checklist', node.canEdit)
        item.iconPath = icon(entry.done ? 'pass' : 'circle-large-outline')
        // Clicking a row ticks or unticks it, but only where the caller may edit.
        if (node.canEdit) item.command = { command: 'prikboard.toggleChecklist', title: 'Tick or untick', arguments: [node] }
        return item
      }
      case 'link': {
        const item = new vscode.TreeItem(node.link.title || node.link.url)
        item.contextValue = contextOf('link')
        item.iconPath = icon('link')
        item.tooltip = node.link.url
        item.command = { command: 'vscode.open', title: 'Open link', arguments: [vscode.Uri.parse(node.link.url)] }
        return item
      }
      case 'assignee': {
        const item = new vscode.TreeItem(node.assignee.name)
        item.contextValue = contextOf('assignee')
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

  private noteItem(note: NoteDetail, canEdit: boolean, where?: string) {
    const items = itemsOf(note)
    const hasDetails = items.length > 0 || linksOf(note).length > 0 || note.assignees.length > 0
    const item = new vscode.TreeItem(
      note.title,
      hasDetails ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None,
    )
    item.contextValue = contextOf('note', canEdit)
    item.iconPath = note.agentWorking ? icon('loading~spin') : icon('note', noteColors[note.color])

    const done = items.filter((entry) => entry.done).length
    item.description = [where ?? '', ...note.tags.map((tag) => `#${tag}`), items.length > 0 ? `${done}/${items.length}` : '']
      .filter(Boolean)
      .join(' ')

    // appendText escapes markdown, so a note cannot format or link its own tooltip.
    const tooltip = new vscode.MarkdownString()
    tooltip.appendText(note.title)
    for (const block of note.blocks) if (block.kind === 'Text' && block.text) tooltip.appendText(`\n\n${block.text}`)
    item.tooltip = tooltip
    return item
  }

  /** Notes assigned to the caller on every board, board by board in the board's own order. */
  private async assignedTo(me: Caller): Promise<Node[]> {
    const boards = await Promise.all((await this.loadBoards()).map((summary) => this.loadBoard(summary.id)))
    return boards.flatMap((board) =>
      this.bucketNodes(board).flatMap((node) =>
        node.kind !== 'bucket'
          ? []
          : node.notes
              .filter((note) => note.assignees.some((assignee) => assignee.principalId === me.id))
              .map((note): Node => ({
                kind: 'note',
                note,
                canEdit: node.canEdit,
                where: `${board.name} · ${node.bucket?.name ?? 'no bucket'}`,
              })),
      ),
    )
  }

  private bucketNodes(board: BoardDetail): Node[] {
    const canEdit = board.yourRole !== 'Viewer'
    const buckets = board.buckets.map((bucket): Node => ({
      kind: 'bucket',
      boardId: board.id,
      bucket,
      notes: board.notes.filter((note) => note.bucketId === bucket.id),
      canEdit,
    }))
    const loose = board.notes.filter((note) => note.bucketId === null)
    return loose.length > 0 ? [...buckets, { kind: 'bucket', boardId: board.id, bucket: null, notes: loose, canEdit }] : buckets
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
