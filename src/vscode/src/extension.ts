import * as vscode from 'vscode'
import { Auth, setting } from './auth'
import { BoardTree, type Node } from './boardTree'
import { registerNoteCommands } from './commands'

export async function activate(context: vscode.ExtensionContext) {
  const auth = new Auth(context.secrets)
  const tree = new BoardTree()
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
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (!event.affectsConfiguration('prikboard')) return
      auth.reconfigure()
      tree.refresh()
      startPolling()
    }),
    vscode.commands.registerCommand('prikboard.signIn', () => auth.signInWithBrowser()),
    vscode.commands.registerCommand('prikboard.signInWithToken', () => auth.signInWithToken()),
    vscode.commands.registerCommand('prikboard.signOut', () => auth.signOut()),
    vscode.commands.registerCommand('prikboard.refresh', () => tree.refresh()),
    vscode.commands.registerCommand('prikboard.openInBrowser', (node?: Node) => {
      const url = node && tree.urlOf(node)
      if (url) void vscode.env.openExternal(vscode.Uri.parse(url))
    }),
    ...registerNoteCommands(tree),
  )

  await auth.init()
}

export function deactivate() {}
