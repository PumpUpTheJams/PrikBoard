import { useEffect, useRef, useState, type RefObject } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardClip, BoardDetail, PastedItems, Point } from '../api/types'
import { bounds } from './selection'
import { itemLists, type Selection, type SelectionKind } from './boardItems'
import { clipSummary, copyCommand, readClip, writeClip } from './clipboard'
import type { ExpandedCardHandle } from './ExpandedCard'
import { imageFiles } from './files'
import { inField } from './keys'
import { GRID, snapPoint } from './layout'
import { ops } from './ops'
import { isBigClip, loadClip, pasteInSteps, type TransferProgress } from './transfer'
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

const aborted = (error: unknown) => error instanceof DOMException && error.name === 'AbortError'

/** A copy on its way to the clipboard: its images being fetched, or done but waiting for a key press to be written. */
export interface Copying {
  cut: boolean
  progress: TransferProgress | null
  /** Loaded, but the browser didn't let it on the clipboard: Ctrl/Cmd+C again (or a click) puts it there. */
  ready: BoardClip | null
  abort: AbortController
}

/** A big paste: being placed (following the pointer), or placed at `at` and on its way. */
export interface Pasting {
  clip: BoardClip
  /** Characters of JSON, for its size. */
  size: number
  /** Where its top-left goes; null while it is being placed. */
  at: Point | null
  /** Where the pointer was when it was pasted, where the outline starts out. */
  start: Point | null
  progress: TransferProgress | null
  abort: AbortController
}

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
 * where the pointer was, or on the open card when the pointer or focus is there. Copies with images show their progress
 * (`copying`), and big pastes are placed first and then show theirs (`pasting`); both can be cancelled.
 */
export function useBoardClipboard(options: BoardClipboardOptions) {
  const { board, readOnly, selected, setSelected, setSelection, settle, notify, lastPointer, moveGroup, addImages, expanded, cardHandle } = options
  const [copying, setCopying] = useState<Copying | null>(null)
  const [pasting, setPasting] = useState<Pasting | null>(null)
  // The latest, for the listeners below.
  const current = useRef({ copying, pasting })
  current.current = { copying, pasting }

  const copied = (clip: BoardClip, cut: boolean) =>
    notify(cut ? `Cut ${clipSummary(clip)}: paste to move it there` : `Copied ${clipSummary(clip)}`)

  /** Puts the selection on the clipboard, with everything on and in it (a bucket's items, a card's images). */
  function copySelected(cut = false) {
    // Loaded already and waiting for this key press.
    if (copying?.ready) {
      putReady()
      return
    }
    copying?.abort.abort()
    const ids = (kind: SelectionKind) => selected.filter((s) => s.kind === kind).map((s) => s.id)
    const what = { cardIds: ids('card'), bucketIds: ids('bucket'), imageIds: ids('image'), drawingIds: ids('drawing'), cut }
    const abort = new AbortController()
    const mine = (c: Copying | null) => c?.abort === abort
    setCopying({ cut, progress: null, ready: null, abort })
    const load = loadClip(board.id, what, (progress) => setCopying((c) => (mine(c) ? { ...c!, progress } : c)), abort.signal)
    // Cut items stay where they are until they are pasted: then they move there.
    writeClip(load).then(
      ({ clip, written }) => {
        if (abort.signal.aborted) return
        if (written) {
          setCopying((c) => (mine(c) ? null : c))
          copied(clip, cut)
        } else {
          setCopying((c) => (mine(c) ? { ...c!, ready: clip } : c))
        }
      },
      (error) => {
        setCopying((c) => (mine(c) ? null : c))
        if (!aborted(error)) notify({ text: `Couldn't ${cut ? 'cut' : 'copy'}: ${errorMessage(error)}`, tone: 'warn' })
      },
    )
  }

  /** Writes a loaded copy that is waiting, now that there is a key press or click to write it with. */
  function putReady() {
    const waiting = current.current.copying
    if (!waiting?.ready) return
    if (copyCommand(waiting.ready)) {
      setCopying(null)
      copied(waiting.ready, waiting.cut)
    } else {
      notify({ text: "The browser didn't allow copying. Try again.", tone: 'warn' })
    }
  }

  function cancelCopy() {
    current.current.copying?.abort.abort()
    setCopying(null)
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
      ...clip.images.filter((i) => !i.cardId && loose(i)).map((i) => ({ kind: 'image' as const, id: i.id ?? '', x: i.x, y: i.y, width: i.width, height: i.height ?? undefined })),
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

  /** Selects what a paste added as the selection rectangle would: the buckets, and what isn't in them. */
  function selectPasted(pasted: PastedItems) {
    const fresh = new Set(pasted.buckets.map((b) => b.id))
    const outside = (item: { bucketId: string | null }) => !item.bucketId || !fresh.has(item.bucketId)
    setSelected([
      ...pasted.buckets.map((b): Selection => ({ kind: 'bucket', id: b.id })),
      ...pasted.cards.filter(outside).map((n): Selection => ({ kind: 'card', id: n.id })),
      ...pasted.images.filter((i) => !i.cardId && outside(i)).map((i): Selection => ({ kind: 'image', id: i.id })),
      ...pasted.drawings.filter((d) => !d.cardId && outside(d)).map((d): Selection => ({ kind: 'drawing', id: d.id })),
    ])
  }

  /**
   * Adds copied items with their top-left where the pointer was (with no pointer yet, wherever the server puts them),
   * and selects them. A big clip is placed first (`pasting` without `at`), then pasted in steps.
   */
  function pasteClip(clip: BoardClip, size: number) {
    const at = lastPointer.current
    if (clip.cut && clip.source.boardShortId === board.shortId && sameHost(clip.source.url)) {
      if (at) moveCutHere(clip, at)
      return
    }
    if (isBigClip(clip, size)) {
      current.current.pasting?.abort.abort()
      setPasting({ clip, size, at: null, start: at, progress: null, abort: new AbortController() })
      return
    }
    const [paste, describe] = ops.paste(board.id, clip, at ? snapPoint(at) : {})
    void settle(async () => {
      const pasted = await paste()
      selectPasted(pasted)
      return pasted
    }, describe)
  }

  /** Pastes the big clip being placed with its top-left at `at`: checked, its images uploaded, then added as one step. */
  function placePaste(at: Point) {
    const placing = current.current.pasting
    if (!placing || placing.at) return
    const spot = snapPoint(at)
    const { abort } = placing
    const mine = (p: Pasting | null) => p?.abort === abort
    setPasting({ ...placing, at: spot })
    pasteInSteps(
      board.id,
      placing.clip,
      spot,
      (progress) => setPasting((p) => (mine(p) ? { ...p!, progress } : p)),
      abort.signal,
      async (clip, files) => {
        let result: PastedItems | undefined
        const [paste, describe] = ops.paste(board.id, clip, spot, 'Paste', files)
        await settle(async () => {
          result = await paste()
          selectPasted(result)
          return result
        }, describe)
        return result
      },
    ).then(
      () => setPasting((p) => (mine(p) ? null : p)),
      (error) => {
        setPasting((p) => (mine(p) ? null : p))
        if (!aborted(error)) notify({ text: `Couldn't paste: ${errorMessage(error)}`, tone: 'warn' })
      },
    )
  }

  /** Stops a big paste before it is saved (once it is, it can only be undone). */
  function cancelPaste() {
    const now = current.current.pasting
    if (!now || now.progress?.step === 'saving') return
    now.abort.abort()
    setPasting(null)
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
      const pastedClip = inField(e.target, { editable: true }) ? null : readClip(e.clipboardData)
      if (pastedClip) {
        e.preventDefault()
        if (readOnly) notify({ text: "You can't add cards to this board.", tone: 'warn' })
        else pasteClip(pastedClip.clip, pastedClip.size)
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

  // Escape stops a copy on its way and a paste being placed or uploaded, before the board's own Escape gets it.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key !== 'Escape') return
      const { copying: copy, pasting: paste } = current.current
      if (paste && paste.progress?.step !== 'saving') {
        e.stopPropagation()
        cancelPaste()
      } else if (copy) {
        e.stopPropagation()
        cancelCopy()
      }
    }
    window.addEventListener('keydown', onKeyDown, { capture: true })
    return () => window.removeEventListener('keydown', onKeyDown, { capture: true })
  })

  // Nothing goes on with the board: a copy or paste on its way is let go.
  useEffect(
    () => () => {
      current.current.copying?.abort.abort()
      current.current.pasting?.abort.abort()
    },
    [board.id],
  )

  return { copySelected, duplicateCard, copying, putReady, cancelCopy, pasting, placePaste, cancelPaste }
}
