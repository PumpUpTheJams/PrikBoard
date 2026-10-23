import * as vscode from 'vscode'
import { Auth, setting } from './auth'
import { BoardTree, cardRouteOf, type Node } from './boardTree'
import { registerCardCommands } from './commands'

export async function activate(context: vscode.ExtensionContext) {
  const auth = new Auth(context.secrets)
  // Before the tree exists: its first requests would go out without the stored credential, and the 401 answers would sign you out.
  await auth.init()
  const tree = new BoardTree(() => auth.me)
  const view = vscode.window.createTreeView('prikboard.boards', { treeDataProvider: tree })

  // Reload while the view is on screen, so changes by other people and agents show up.
  let timer: ReturnType<typeof setInterval> | undefined
  const stopPolling = () => {
    if (timer) clearInterval(timer)
    timer = undefined
  }
  const startPolling = () => {
    stopPolling()
    if (auth.signedIn && view.visible) {
      timer = setInterval(() => tree.refresh(), Math.max(5, setting('refreshSeconds', 15)) * 1000)
    }
  }

  context.subscriptions.push(
    auth,
    tree,
    view,
    { dispose: stopPolling },
    view.onDidChangeVisibility(startPolling),
    auth.onDidChange(() => {
      tree.refresh()
      startPolling()
    }),
    // "Assigned to you" needs to know who you are, which loads after signing in.
    auth.onDidChangeMe(() => tree.refresh()),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (!event.affectsConfiguration('prikboard')) return
      auth.reconfigure()
      tree.refresh()
      startPolling()
    }),
    vscode.commands.registerCommand('prikboard.signIn', () => auth.signInWithBrowser()),
    vscode.commands.registerCommand('prikboard.signInWithToken', () => auth.signInWithToken()),
    vscode.commands.registerCommand('prikboard.signOut', () => auth.signOut()),
    vscode.commands.registerCommand('prikboard.chooseServer', () => auth.chooseServer()),
    vscode.commands.registerCommand('prikboard.refresh', () => tree.refresh()),
    vscode.commands.registerCommand('prikboard.openInBrowser', (node?: Node) => {
      const url = node && tree.urlOf(node)
      if (url) void vscode.env.openExternal(vscode.Uri.parse(url))
    }),
    // Clicking a link to a card shows that card in the tree, or opens it in the browser when it isn't there.
    vscode.commands.registerCommand('prikboard.openLink', async (node?: Node) => {
      if (node?.kind !== 'link') return
      const route = cardRouteOf(node.link)
      if (!route) return
      const card = await tree.cardNode(route, node.under.where !== undefined).catch(() => undefined)
      if (card) {
        try {
          await view.reveal(card, { select: true, focus: true })
          return
        } catch {
          // The row couldn't be found; the browser still shows the card.
        }
      }
      const url = tree.urlOf(node)
      if (url) void vscode.env.openExternal(vscode.Uri.parse(url))
    }),
    ...registerCardCommands(tree),
  )

  startPolling()
}

export function deactivate() {}
