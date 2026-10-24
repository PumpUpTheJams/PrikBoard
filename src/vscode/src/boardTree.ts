import * as vscode from 'vscode'
import { api, errorMessage } from '../../web/src/api/client'
import type {
  Assignee,
  BoardDetail,
  BoardSummary,
  BucketDetail,
  Caller,
  CardComponent,
  CardColor,
  CardDetail,
  CardLink,
} from '../../web/src/api/types'
import { cardPath, cardRoute, type CardRoute } from '../../web/src/api/cardLinks'
import { webUrl } from './auth'

/**
 * `canEdit` is false on boards where the caller is a Viewer; it decides which commands a row offers.
 */
export type Node =
  /** The cards assigned to the signed-in caller, across all their boards. */
  | { kind: 'mine'; me: Caller }
  | { kind: 'board'; board: BoardSummary }
  /** `bucket` is null for cards that sit in no bucket. */
  | { kind: 'bucket'; boardId: string; bucket: BucketDetail | null; cards: CardDetail[]; canEdit: boolean }
  /**
   * A bucket under "Assigned to you": the caller's cards in every bucket of that name, on all boards (so "Doing" is one
   * row), collapsible on its own. `name` is null for cards that sit in no bucket.
   */
  | { kind: 'mineBucket'; name: string | null; cards: CardNode[] }
  /** `where` (the board) is set where a card is shown outside its board, under "Assigned to you". */
  | { kind: 'card'; card: CardDetail; canEdit: boolean; where?: string }
  /** One item of one of the card's checklist components. */
  | { kind: 'checklist'; card: CardDetail; component: CardComponent; index: number; canEdit: boolean }
  /** `under` is the card row it sits under, so following a link to a card knows where it was clicked. */
  | { kind: 'link'; link: CardLink; under: CardNode }
  | { kind: 'assignee'; assignee: Assignee }

export type CardNode = Extract<Node, { kind: 'card' }>

const cardColors: Record<CardColor, string> = {
  Butter: 'charts.yellow',
  Mint: 'charts.green',
  Sky: 'charts.blue',
  Coral: 'charts.red',
  Lilac: 'charts.purple',
}

/** A card's content is components; the tree shows the items of all its checklists and all its links. */
const checklistsOf = (card: CardDetail) => card.components.filter((b) => b.kind === 'Checklist')
const itemsOf = (card: CardDetail) => checklistsOf(card).flatMap((b) => b.items ?? [])
const linksOf = (card: CardDetail) => card.components.flatMap((b) => b.links ?? [])

/** How a card is named in a row, a tooltip or a prompt: its title, or a stand-in when it has none. */
export const titleOf = (card: CardDetail) => card.title || 'Untitled card'

const icon = (id: string, color?: string) => new vscode.ThemeIcon(id, color ? new vscode.ThemeColor(color) : undefined)

/** The PrikBoard site that full card links must be on to count as card links, or '' when webUrl isn't a URL. */
const webOrigin = () => {
  try {
    return new URL(webUrl()).origin
  } catch {
    return ''
  }
}

/** The card a link points at, when it is a card's path or a full link to it on this server. */
export const cardRouteOf = (link: CardLink) => cardRoute(link.url, webOrigin())

/** Buckets of the same name on different boards share one row under "Assigned to you". */
const bucketKey = (name: string | null) => (name === null ? '' : `:${name.trim().toLowerCase()}`)

/** How a card under "Assigned to you" says where it is: its bucket is the row above, so only the board. */
const whereOf = (board: BoardDetail) => board.name

/** Menus in package.json match these, for example `viewItem == card.edit`. */
const contextOf = (kind: string, canEdit = false) => (canEdit ? `${kind}.edit` : kind)

/**
 * "Assigned to you", then boards with their buckets, cards and a card's checklist, links and assignees. A board loads
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

  /** The buckets of a board that is open in the tree, in list order (oldest first). */
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
          await Promise.all((await this.loadBoards()).map((summary) => this.loadBoard(summary.id)))
          return this.assignedTo(node.me)
        case 'mineBucket':
          return node.cards
        case 'board':
          return this.bucketNodes(await this.loadBoard(node.board.id))
        case 'bucket':
          return node.cards.map((card): Node => ({ kind: 'card', card, canEdit: node.canEdit }))
        case 'card':
          return [
            ...checklistsOf(node.card).flatMap((component) =>
              (component.items ?? []).map((_, index): Node => ({ kind: 'checklist', card: node.card, component, index, canEdit: node.canEdit })),
            ),
            ...linksOf(node.card).map((link): Node => ({ kind: 'link', link, under: node })),
            ...node.card.assignees.map((assignee): Node => ({ kind: 'assignee', assignee })),
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
        item.id = 'mine'
        item.contextValue = contextOf('mine')
        item.iconPath = icon('account')
        item.description = node.me.displayName
        item.tooltip = `Cards assigned to ${node.me.displayName}, on all your boards`
        return item
      }
      case 'board': {
        const item = new vscode.TreeItem(node.board.name, vscode.TreeItemCollapsibleState.Collapsed)
        item.id = `board:${node.board.id}`
        item.contextValue = contextOf('board')
        item.iconPath = icon('project')
        // Always shown: it decides whether the edit commands appear.
        item.description = node.board.yourRole === 'Viewer' ? 'view only' : node.board.yourRole
        item.tooltip = node.board.description ?? node.board.name
        return item
      }
      case 'bucket': {
        const item = new vscode.TreeItem(node.bucket?.name ?? 'No bucket', vscode.TreeItemCollapsibleState.Expanded)
        item.id = `bucket:${node.boardId}:${node.bucket?.id ?? 'loose'}`
        item.contextValue = contextOf('bucket', node.canEdit)
        item.iconPath = icon('circle-filled')
        item.description = String(node.cards.length)
        return item
      }
      case 'mineBucket': {
        const item = new vscode.TreeItem(node.name ?? 'No bucket', vscode.TreeItemCollapsibleState.Expanded)
        // Stable per name, so VS Code remembers which ones were collapsed.
        item.id = `mine:bucket${bucketKey(node.name)}`
        item.contextValue = contextOf('mineBucket')
        item.iconPath = icon('circle-filled')
        item.description = String(node.cards.length)
        return item
      }
      case 'card': {
        const item = this.cardItem(node.card, node.canEdit, node.where)
        // A card can show twice: under its board and under "Assigned to you".
        item.id = `${node.where === undefined ? '' : 'mine:'}card:${node.card.id}`
        return item
      }
      case 'checklist': {
        const entry = node.component.items![node.index]
        const item = new vscode.TreeItem(entry.text)
        item.contextValue = contextOf('checklist', node.canEdit)
        item.iconPath = icon(entry.done ? 'pass' : 'circle-large-outline')
        // Clicking a row ticks or unticks it, but only where the caller may edit.
        if (node.canEdit) item.command = { command: 'prikboard.toggleChecklist', title: 'Tick or untick', arguments: [node] }
        return item
      }
      case 'link': {
        const item = new vscode.TreeItem(node.link.title || node.link.url)
        const toCard = cardRouteOf(node.link) !== null
        // `link.card` rows get "Open in PrikBoard"; clicking them shows the card in the tree.
        item.contextValue = contextOf(toCard ? 'link.card' : 'link')
        item.iconPath = icon(toCard ? 'note' : 'link')
        item.tooltip = node.link.url
        item.command = toCard
          ? { command: 'prikboard.openLink', title: 'Show linked card', arguments: [node] }
          : { command: 'vscode.open', title: 'Open link', arguments: [vscode.Uri.parse(node.link.url)] }
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

  /** The web app address of a board, card or card link row, for "Open in PrikBoard". */
  urlOf(node: Node): string | undefined {
    switch (node.kind) {
      case 'board':
        return `${webUrl()}/#/boards/${node.board.shortId}`
      case 'card':
        return webUrl() + node.card.path
      case 'link': {
        const route = cardRouteOf(node.link)
        return route ? webUrl() + cardPath(route) : undefined
      }
      default:
        return undefined
    }
  }

  /** The row above a row, so the view can reveal cards. Only buckets, cards and links need one. */
  getParent(node: Node): Node | undefined {
    switch (node.kind) {
      case 'bucket': {
        const board = this.boards?.find((summary) => summary.id === node.boardId)
        return board && { kind: 'board', board }
      }
      case 'mineBucket': {
        const me = this.me()
        return me && { kind: 'mine', me }
      }
      case 'card': {
        if (node.where !== undefined) {
          const me = this.me()
          return me && this.assignedTo(me).find((group) => group.cards.some((card) => card.card.id === node.card.id))
        }
        const board = this.details.get(node.card.boardId)
        return board && this.bucketNodes(board).find((bucket) => bucket.kind === 'bucket' && (bucket.bucket?.id ?? null) === node.card.bucketId)
      }
      case 'link':
        return node.under
      default:
        return undefined
    }
  }

  /**
   * The row of the card a link points at, or undefined when it isn't on the caller's boards. `assignedRow` asks for its
   * row under "Assigned to you" when it is assigned to the caller. Data loaded earlier may miss a newer card, so a miss
   * loads everything once more.
   */
  async cardNode(route: CardRoute, assignedRow: boolean): Promise<CardNode | undefined> {
    const find = async (): Promise<CardNode | undefined> => {
      const summary = (await this.loadBoards()).find((board) => board.id === route.board || board.shortId === route.board)
      if (!summary) return undefined
      const board = await this.loadBoard(summary.id)
      const card = board.cards.find((c) => c.id === route.card || String(c.number) === route.card)
      if (!card) return undefined
      const canEdit = board.yourRole !== 'Viewer'
      const me = this.me()
      return assignedRow && me && card.assignees.some((assignee) => assignee.principalId === me.id)
        ? { kind: 'card', card, canEdit, where: whereOf(board) }
        : { kind: 'card', card, canEdit }
    }
    const found = await find()
    if (found) return found
    this.refresh()
    return find()
  }

  private cardItem(card: CardDetail, canEdit: boolean, where?: string) {
    const items = itemsOf(card)
    const hasDetails = items.length > 0 || linksOf(card).length > 0 || card.assignees.length > 0
    const item = new vscode.TreeItem(
      titleOf(card),
      hasDetails ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None,
    )
    item.contextValue = contextOf('card', canEdit)
    item.iconPath = card.agentWorking ? icon('loading~spin') : icon('note', cardColors[card.color]) // 'note' is the codicon's name

    const done = items.filter((entry) => entry.done).length
    item.description = [where ?? '', ...card.tags.map((tag) => `#${tag}`), items.length > 0 ? `${done}/${items.length}` : '']
      .filter(Boolean)
      .join(' ')

    // appendText escapes markdown, so a card cannot format or link its own tooltip.
    const tooltip = new vscode.MarkdownString()
    tooltip.appendText(titleOf(card))
    for (const component of card.components) if (component.kind === 'Text' && component.text) tooltip.appendText(`\n\n${component.text}`)
    item.tooltip = tooltip
    return item
  }

  /**
   * Cards assigned to the caller on every loaded board, grouped by bucket name. Buckets come in the order they first
   * appear (boards in list order, each board's buckets in its own order), cards without a bucket last; buckets without
   * the caller's cards are left out. Synchronous so getParent can use it: getChildren loads every board first.
   */
  private assignedTo(me: Caller): Extract<Node, { kind: 'mineBucket' }>[] {
    const groups = new Map<string, Extract<Node, { kind: 'mineBucket' }>>()
    let loose: Extract<Node, { kind: 'mineBucket' }> | undefined
    for (const summary of this.boards ?? []) {
      const board = this.details.get(summary.id)
      if (!board) continue
      for (const node of this.bucketNodes(board)) {
        if (node.kind !== 'bucket') continue
        const cards = node.cards
          .filter((card) => card.assignees.some((assignee) => assignee.principalId === me.id))
          .map((card): CardNode => ({ kind: 'card', card, canEdit: node.canEdit, where: whereOf(board) }))
        if (cards.length === 0) continue
        const name = node.bucket?.name ?? null
        if (name === null) {
          loose ??= { kind: 'mineBucket', name: null, cards: [] }
          loose.cards.push(...cards)
          continue
        }
        const key = bucketKey(name)
        const group = groups.get(key) ?? { kind: 'mineBucket', name, cards: [] }
        group.cards.push(...cards)
        groups.set(key, group)
      }
    }
    return loose ? [...groups.values(), loose] : [...groups.values()]
  }

  private bucketNodes(board: BoardDetail): Node[] {
    const canEdit = board.yourRole !== 'Viewer'
    const buckets = board.buckets.map((bucket): Node => ({
      kind: 'bucket',
      boardId: board.id,
      bucket,
      cards: board.cards.filter((card) => card.bucketId === bucket.id),
      canEdit,
    }))
    const loose = board.cards.filter((card) => card.bucketId === null)
    return loose.length > 0 ? [...buckets, { kind: 'bucket', boardId: board.id, bucket: null, cards: loose, canEdit }] : buckets
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
