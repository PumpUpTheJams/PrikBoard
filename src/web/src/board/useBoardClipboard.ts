import { useEffect, type RefObject } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardClip, BoardDetail, Point } from '../api/types'
import { bounds } from './selection'
import { itemLists, type Selection, type SelectionKind } from './boardItems'
import { clipSummary, readClip, writeClip } from './clipboard'
import type { ExpandedCardHandle } from './ExpandedCard'
import { imageFiles } from './files'
import { inField } from './keys'
import { GRID, snapPoint } from './layout'
import { ops } from './ops'
import { created, snap, step, target, type RecordingRun } from './undo'
import type { Notify } from './useToast'

/** Whether a clip's server is the one this page is on (its scheme may differ behind a proxy). */
function sameHost(url: string) {
  try {
    return new URL(url).host === window.location.host
  } catch {
    return false
  }
}

/** Whether text on the page is selected, which Ctrl/Cmd+C then copies as usual instead of the selected items. */
export const textSelected = () => (window.getSelection()?.toString().trim() ?? '') !== ''

interface BoardClipboardOptions {
  board: BoardDetail
  readOnly: boolean
  selected: Selection[]
  setSelected: (items: Selection[]) => void
  setSelection: (item: Selection | null) => void
  /** Runs a change through the board, recorded for undo, then lets go of the finished gesture. */
  settle: RecordingRun
  notify: Notify
  /** The last pointer position over the canvas, in canvas pixels: where pasted items go. */
  lastPointer: RefObject<Point | null>
  /** Saves a group move as one step (moveGroup), shifted by `by`. */
  moveGroup: (members: Selection[], label: string | undefined, by: Point) => void
  addImages: (files: File[], place: { at?: Point; cardId?: string }) => Promise<void>
  /** The open card. */
  expanded: { id: string } | null
  cardHandle: RefObject<ExpandedCardHandle | null>
}

/**
 * Copy, cut, paste and duplicate on the board, through the system clipboard. Pasting an image puts it on the board
 * where the pointer was, or on the open card when the pointer or focus is there.
 */
export function useBoardClipboard(options: BoardClipboardOptions) {
  const { board, readOnly, selected, setSelected, setSelection, settle, notify, lastPointer, moveGroup, addImages, expanded, cardHandle } = options

  /** Puts the selection on the clipboard, with everything on and in it (a bucket's items, a card's images). */
  function copySelected(cut = false) {
    const ids = (kind: SelectionKind) => selected.filter((s) => s.kind === kind).map((s) => s.id)
    const what = { cardIds: ids('card'), bucketIds: ids('bucket'), imageIds: ids('image'), drawingIds: ids('drawing'), cut }
    // Cut items stay where they are until they are pasted: then they move there.
    writeClip(api.copyItems(board.id, what)).then(
      (clip) => notify(cut ? `Cut ${clipSummary(clip)}: paste to move it there` : `Copied ${clipSummary(clip)}`),
      (error) => notify({ text: `Couldn't ${cut ? 'cut' : 'copy'}: ${errorMessage(error)}`, tone: 'warn' }),
    )
  }

  /**
   * A cut pasted on the board it was cut from: the items still here move, as one step, so their top-left lands at
   * `at`. Cards keep their numbers, unlike a paste.
   */
  function moveCutHere(clip: BoardClip, at: Point) {
    const loose = (item: { bucketId?: string | null }) => !item.bucketId
    const tops = [
      ...(clip.buckets ?? []).map((b) => ({ kind: 'bucket' as const, ...b })),
      ...clip.cards.filter(loose).map((n) => ({ kind: 'card' as const, ...n })),
      ...clip.images.filter((i) => !i.cardId && loose(i)).map((i) => ({ kind: 'image' as const, ...i, id: i.id ?? '' })),
      ...clip.drawings.filter((d) => !d.cardId && loose(d)).map((d) => ({ kind: 'drawing' as const, ...d, id: d.id ?? '' })),
    ]
    const here = itemLists(board)
    const members = tops.filter((t) => here[t.kind].some((x) => x.id === t.id)).map((t): Selection => ({ kind: t.kind, id: t.id }))
    if (members.length === 0) {
      notify({ text: 'These were moved already.', tone: 'warn' })
      return
    }
    const from = snapPoint(bounds(tops)!)
    const to = snapPoint(at)
    moveGroup(members, undefined, { x: to.x - from.x, y: to.y - from.y })
    setSelected(members)
  }

  /**
   * Adds copied items with their top-left where the pointer was (with no pointer yet, wherever the server puts them),
   * and selects them as the selection rectangle would: the buckets and what isn't in them.
   */
  function pasteClip(clip: BoardClip) {
    const at = lastPointer.current
    if (clip.cut && clip.source.boardShortId === board.shortId && sameHost(clip.source.url)) {
      if (at) moveCutHere(clip, at)
      return
    }
    const [paste, describe] = ops.paste(board.id, clip, at ? snapPoint(at) : {})
    void settle(async () => {
      const pasted = await paste()
      const fresh = new Set(pasted.buckets.map((b) => b.id))
      const outside = (item: { bucketId: string | null }) => !item.bucketId || !fresh.has(item.bucketId)
      setSelected([
        ...pasted.buckets.map((b): Selection => ({ kind: 'bucket', id: b.id })),
        ...pasted.cards.filter(outside).map((n): Selection => ({ kind: 'card', id: n.id })),
        ...pasted.images.filter((i) => !i.cardId && outside(i)).map((i): Selection => ({ kind: 'image', id: i.id })),
        ...pasted.drawings.filter((d) => !d.cardId && outside(d)).map((d): Selection => ({ kind: 'drawing', id: d.id })),
      ])
      return pasted
    }, describe)
  }

  /** Copies a card with everything on it one grid step down and right of it, and selects the copy. */
  function duplicateCard(id: string) {
    const card = board.cards.find((n) => n.id === id)
    if (!card) return
    const at = snapPoint({ x: card.x + GRID, y: card.y + GRID })
    void settle(
      async () => {
        const pasted = await api.pasteItems(board.id, await api.copyItems(board.id, { cardIds: [card.id] }), at)
        setSelection({ kind: 'card', id: pasted.cards[0].id })
        return pasted
      },
      (pasted) => step('Duplicate card', pasted.cards.map((copy) => created(target.card(copy.id), snap.card(copy)))),
    )
  }

  useEffect(() => {
    function onPaste(e: ClipboardEvent) {
      const clip = inField(e.target, { editable: true }) ? null : readClip(e.clipboardData)
      if (clip) {
        e.preventDefault()
        if (readOnly) notify({ text: "You can't add cards to this board.", tone: 'warn' })
        else pasteClip(clip)
        return
      }
      const files = imageFiles(e.clipboardData)
      if (readOnly || files.length === 0) return
      e.preventDefault()
      const card = cardHandle.current
      void addImages(
        files,
        expanded && card?.wantsPaste() ? { cardId: expanded.id, at: card.pastePoint() } : { at: lastPointer.current ?? undefined },
      )
    }
    window.addEventListener('paste', onPaste)
    return () => window.removeEventListener('paste', onPaste)
  })

  return { copySelected, duplicateCard }
}
