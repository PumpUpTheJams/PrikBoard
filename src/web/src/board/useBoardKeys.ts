import { useEffect } from 'react'
import type { Selection } from './boardItems'
import { keyTargetIgnored, shortcut } from './keys'
import { toolKeys, type Tool } from './tools'
import { textSelected } from './useBoardClipboard'

interface BoardKeysOptions {
  readOnly: boolean
  /** A card is open (Escape is its own then). */
  cardOpen: boolean
  selected: Selection[]
  selection: Selection | null
  /** A gesture, a pen stroke or a drag in the open card is in progress. */
  busy: { gesture: boolean; stroke: boolean; card: boolean }
  setTool: (tool: Tool) => void
  setSelection: (item: Selection | null) => void
  copySelected: (cut?: boolean) => void
  deleteSelected: () => void
  deleteOne: (item: Selection) => void
  duplicateCard: (id: string) => void
  selectAll: () => void
  addImagesFromPicker: () => void
  undo: { undo: () => void; redo: () => void }
}

/** The board's keys: tools, Escape, select all, copy, cut, delete and duplicate, and undo and redo (also in the open card). */
export function useBoardKeys(options: BoardKeysOptions) {
  const { readOnly, cardOpen, selected, selection, busy, setTool, setSelection, copySelected, deleteSelected, deleteOne, duplicateCard, selectAll, addImagesFromPicker, undo } = options

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      // The open card is a window of its own: keys typed in it are its own, and Escape closes it.
      if (keyTargetIgnored(e.target)) return
      const copying = () => selected.length > 0 && !textSelected()
      const deleting = e.key === 'Delete' || e.key === 'Backspace'
      if (e.key === 'Escape') {
        if (cardOpen) return
        setTool('select')
        setSelection(null)
      } else if (shortcut(e, 'a', { shift: false })) {
        e.preventDefault()
        if (!busy.gesture) selectAll()
      } else if (shortcut(e, 'c', { shift: false }) && copying()) {
        // Viewers may copy too: the copies go on boards where they can edit.
        e.preventDefault()
        if (!e.repeat) copySelected()
      } else if (shortcut(e, 'x', { shift: false }) && !readOnly && copying()) {
        e.preventDefault()
        if (!e.repeat) copySelected(true)
      } else if (readOnly) {
        if (e.key.toLowerCase() === 'v') setTool('select')
      } else if (deleting && selected.length > 1) {
        // Buckets go too (what is in them stays). The toast's Undo puts them all back.
        e.preventDefault()
        if (!e.repeat && !busy.gesture) deleteSelected()
      } else if (deleting && selection && selection.kind !== 'bucket') {
        // No question first: the toast that follows has an Undo button, and Ctrl/Cmd+Z works too.
        e.preventDefault()
        if (!e.repeat) deleteOne(selection)
      } else if (shortcut(e, 'd') && selection?.kind === 'card') {
        e.preventDefault()
        if (!e.repeat) duplicateCard(selection.id)
      } else if (e.ctrlKey || e.metaKey || e.altKey) {
        return
      } else if (e.key.toLowerCase() === 'i') {
        addImagesFromPicker()
      } else if (toolKeys[e.key.toLowerCase()]) {
        setTool(toolKeys[e.key.toLowerCase()])
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  // Ctrl/Cmd+Z undoes, Ctrl/Cmd+Shift+Z or Ctrl+Y redoes. In a field the browser undoes typing instead, and other
  // windows keep the keys.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      const redo = shortcut(e, 'z', { shift: true }) || shortcut(e, 'y', { shift: false })
      if (!redo && !shortcut(e, 'z')) return
      if (keyTargetIgnored(e.target, { editable: true, inCard: true })) return
      e.preventDefault()
      if (readOnly || busy.gesture || busy.stroke || busy.card) return
      if (redo) undo.redo()
      else undo.undo()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })
}
