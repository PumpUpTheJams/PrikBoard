import * as vscode from 'vscode'
import { api, errorMessage } from '../../web/src/api/client'
import { titleOf, type BoardTree, type Node } from './boardTree'

const required = (value: string) => (value.trim() ? undefined : 'Enter a title')

/** "a, #b  c" becomes ["a", "b", "c"]. The API normalizes the tags further. */
const parseTags = (text: string) =>
  text
    .split(/[\s,]+/)
    .map((tag) => tag.replace(/^#+/, ''))
    .filter(Boolean)

/**
 * Commands that change cards. Each one is offered on rows of boards the caller may edit (see `contextOf` in boardTree.ts),
 * calls the API and then reloads the tree. The API stays the judge: a refused change is shown as the server's message.
 */
export function registerCardCommands(tree: BoardTree): vscode.Disposable[] {
  const run = async (change: () => Promise<unknown>) => {
    try {
      await change()
    } catch (error) {
      void vscode.window.showErrorMessage(`PrikBoard: ${errorMessage(error)}`)
      return
    }
    tree.refresh()
  }

  const newCard = async (node?: Node) => {
    if (node?.kind !== 'bucket') return
    const title = await vscode.window.showInputBox({
      title: `New card in ${node.bucket?.name ?? 'this board'}`,
      prompt: 'Title',
      validateInput: required,
    })
    if (title === undefined) return
    // Without a bucket id the server puts the card into the first bucket's free spot.
    await run(() => api.createCard(node.boardId, { title: title.trim(), bucketId: node.bucket?.id }))
  }

  const renameCard = async (node?: Node) => {
    if (node?.kind !== 'card') return
    const title = await vscode.window.showInputBox({ title: 'Rename card', value: node.card.title, validateInput: required })
    if (title === undefined || title.trim() === node.card.title) return
    await run(() => api.updateCard(node.card.id, { title: title.trim() }))
  }

  const editTags = async (node?: Node) => {
    if (node?.kind !== 'card') return
    const text = await vscode.window.showInputBox({
      title: 'Tags',
      prompt: 'Separate tags with spaces or commas. Leave empty to remove all tags.',
      value: node.card.tags.join(' '),
    })
    if (text === undefined) return
    await run(() => api.updateCard(node.card.id, { tags: parseTags(text) }))
  }

  const moveCard = async (node?: Node) => {
    if (node?.kind !== 'card') return
    const buckets = tree.bucketsOf(node.card.boardId)
    const picked = await vscode.window.showQuickPick(
      buckets.map((bucket) => ({
        label: bucket.name,
        description: bucket.id === node.card.bucketId ? 'current' : undefined,
        bucketId: bucket.id,
      })),
      { title: `Move "${titleOf(node.card)}" to` },
    )
    if (!picked || picked.bucketId === node.card.bucketId) return
    await run(() => api.moveCard(node.card.id, { bucketId: picked.bucketId }))
  }

  const toggleChecklist = async (node?: Node) => {
    if (node?.kind !== 'checklist') return
    // A given list replaces the component's whole list, so send every item of that checklist with the one change.
    const items = (node.component.items ?? []).map((entry, index) => (index === node.index ? { ...entry, done: !entry.done } : entry))
    await run(() => api.updateCardComponent(node.card.id, node.component.id, { items }))
  }

  const toggleAgentWorking = async (node?: Node) => {
    if (node?.kind !== 'card') return
    await run(() => api.updateCard(node.card.id, { agentWorking: !node.card.agentWorking }))
  }

  const deleteCard = async (node?: Node) => {
    if (node?.kind !== 'card') return
    const answer = await vscode.window.showWarningMessage(
      `Delete "${titleOf(node.card)}"?`,
      { modal: true, detail: 'It can be put back from Deleted items in the web app for 7 days.' },
      'Delete',
    )
    if (answer !== 'Delete') return
    await run(() => api.deleteCard(node.card.id))
  }

  return [
    vscode.commands.registerCommand('prikboard.newCard', newCard),
    vscode.commands.registerCommand('prikboard.renameCard', renameCard),
    vscode.commands.registerCommand('prikboard.editTags', editTags),
    vscode.commands.registerCommand('prikboard.moveCard', moveCard),
    vscode.commands.registerCommand('prikboard.toggleChecklist', toggleChecklist),
    vscode.commands.registerCommand('prikboard.toggleAgentWorking', toggleAgentWorking),
    vscode.commands.registerCommand('prikboard.deleteCard', deleteCard),
  ]
}
