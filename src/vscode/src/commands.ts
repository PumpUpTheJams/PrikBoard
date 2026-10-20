import * as vscode from 'vscode'
import { api, errorMessage } from '../../web/src/api/client'
import type { BoardTree, Node } from './boardTree'

const required = (value: string) => (value.trim() ? undefined : 'Enter a title')

/** "a, #b  c" becomes ["a", "b", "c"]. The API normalizes the tags further. */
const parseTags = (text: string) =>
  text
    .split(/[\s,]+/)
    .map((tag) => tag.replace(/^#+/, ''))
    .filter(Boolean)

/**
 * Commands that change notes. Each one is offered on rows of boards the caller may edit (see `contextOf` in boardTree.ts),
 * calls the API and then reloads the tree. The API stays the judge: a refused change is shown as the server's message.
 */
export function registerNoteCommands(tree: BoardTree): vscode.Disposable[] {
  const run = async (change: () => Promise<unknown>) => {
    try {
      await change()
    } catch (error) {
      void vscode.window.showErrorMessage(`PrikBoard: ${errorMessage(error)}`)
      return
    }
    tree.refresh()
  }

  const newNote = async (node?: Node) => {
    if (node?.kind !== 'bucket') return
    const title = await vscode.window.showInputBox({
      title: `New note in ${node.bucket?.name ?? 'this board'}`,
      prompt: 'Title',
      validateInput: required,
    })
    if (title === undefined) return
    // Without a bucket id the server puts the note into the first bucket's free spot.
    await run(() => api.createNote(node.boardId, { title: title.trim(), bucketId: node.bucket?.id }))
  }

  const renameNote = async (node?: Node) => {
    if (node?.kind !== 'note') return
    const title = await vscode.window.showInputBox({ title: 'Rename note', value: node.note.title, validateInput: required })
    if (title === undefined || title.trim() === node.note.title) return
    await run(() => api.updateNote(node.note.id, { title: title.trim() }))
  }

  const editTags = async (node?: Node) => {
    if (node?.kind !== 'note') return
    const text = await vscode.window.showInputBox({
      title: 'Tags',
      prompt: 'Separate tags with spaces or commas. Leave empty to remove all tags.',
      value: node.note.tags.join(' '),
    })
    if (text === undefined) return
    await run(() => api.updateNote(node.note.id, { tags: parseTags(text) }))
  }

  const moveNote = async (node?: Node) => {
    if (node?.kind !== 'note') return
    const buckets = tree.bucketsOf(node.note.boardId)
    const picked = await vscode.window.showQuickPick(
      buckets.map((bucket) => ({
        label: bucket.name,
        description: bucket.id === node.note.bucketId ? 'current' : undefined,
        bucketId: bucket.id,
      })),
      { title: `Move "${node.note.title || 'Untitled post-it'}" to` },
    )
    if (!picked || picked.bucketId === node.note.bucketId) return
    await run(() => api.moveNote(node.note.id, { bucketId: picked.bucketId }))
  }

  const toggleChecklist = async (node?: Node) => {
    if (node?.kind !== 'checklist') return
    // A given list replaces the block's whole list, so send every item of that checklist with the one change.
    const items = (node.block.items ?? []).map((entry, index) => (index === node.index ? { ...entry, done: !entry.done } : entry))
    await run(() => api.updateNoteBlock(node.note.id, node.block.id, { items }))
  }

  const toggleAgentWorking = async (node?: Node) => {
    if (node?.kind !== 'note') return
    await run(() => api.updateNote(node.note.id, { agentWorking: !node.note.agentWorking }))
  }

  const deleteNote = async (node?: Node) => {
    if (node?.kind !== 'note') return
    const answer = await vscode.window.showWarningMessage(
      `Delete "${node.note.title || 'Untitled post-it'}"?`,
      { modal: true, detail: 'It can be put back from Deleted items in the web app for 7 days.' },
      'Delete',
    )
    if (answer !== 'Delete') return
    await run(() => api.deleteNote(node.note.id))
  }

  return [
    vscode.commands.registerCommand('prikboard.newNote', newNote),
    vscode.commands.registerCommand('prikboard.renameNote', renameNote),
    vscode.commands.registerCommand('prikboard.editTags', editTags),
    vscode.commands.registerCommand('prikboard.moveNote', moveNote),
    vscode.commands.registerCommand('prikboard.toggleChecklist', toggleChecklist),
    vscode.commands.registerCommand('prikboard.toggleAgentWorking', toggleAgentWorking),
    vscode.commands.registerCommand('prikboard.deleteNote', deleteNote),
  ]
}
