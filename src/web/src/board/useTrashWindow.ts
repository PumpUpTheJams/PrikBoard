import { useRef, useState } from 'react'
import { api } from '../api/client'
import type { BoardDetail, CardDetail, DropBucket, ImageDetail, Point, Rect } from '../api/types'
import type { ToastMessage } from '../components/Toast'
import { CARD_HEIGHT, CARD_WIDTH, snapPoint } from './layout'
import { trashInHash } from './links'
import type { OutgoingCard, TrashWindowHandle } from './TrashWindow'
import { created, snap, step, target, type RecordingRun } from './undo'

interface TrashWindowOptions {
  board: BoardDetail
  /** Runs a change through the board, recorded for undo. */
  runRec: RecordingRun
  /** The same, letting go of the board's finished gesture afterwards. */
  settle: RecordingRun
  notify: (message: string | Omit<ToastMessage, 'id'>) => void
  /** A card went back on the board. */
  onPutBack: (card: CardDetail) => void
  /** A screen point in board canvas pixels when it is over the board and not over the open card; else null. */
  boardPoint: (client: Point) => Point | null
  /** The bucket a drop with the pointer at a canvas point goes into, or loose (`dropBucket`). */
  dropBucket: (pointer: Point, item?: Rect) => DropBucket
}

/** The trash window: cards deleted in the last 7 days, opened from the board menu (through the address). */
export function useTrashWindow({ board, runRec, settle, notify, onPutBack, boardPoint, dropBucket }: TrashWindowOptions) {
  const [open, setOpen] = useState(() => !board.visiting && trashInHash())
  const ref = useRef<TrashWindowHandle>(null)
  /** A card in the trash shown as it is inside, read only. */
  const [preview, setPreview] = useState<{ card: CardDetail; images: ImageDetail[] } | null>(null)
  /** A card being dragged out of the window. */
  const [outgoing, setOutgoing] = useState<OutgoingCard | null>(null)
  /** A drag in progress in the window. */
  const [busy, setBusy] = useState(false)

  /** Puts a card from the trash back on the board: where it was, or at `at` (dragged out of the window). */
  function putBack(card: CardDetail, at?: Point & DropBucket) {
    if (preview?.card.id === card.id) setPreview(null)
    void settle(
      () => api.restoreCard(card.id, at),
      (after) => step('Put back card', [created(target.card(after.id), snap.card(after))]),
    ).then((ok) => {
      if (!ok) return
      notify('Put back on the board')
      onPutBack(card)
    })
  }

  /** Deletes a card in the trash for good. That can't be undone, so it records no step. */
  async function purge(card: CardDetail) {
    if (preview?.card.id === card.id) setPreview(null)
    const ok = await runRec(() => api.purgeCard(board.id, card.id))
    if (ok) notify('Deleted for good')
    return ok
  }

  /** Deletes everything in the trash for good (the window asks first). */
  async function empty() {
    setPreview(null)
    let purged = 0
    const ok = await runRec(async () => {
      purged = (await api.emptyTrash(board.id)).purged
    })
    if (ok) notify(purged === 1 ? '1 card deleted for good' : `${purged} cards deleted for good`)
    return ok
  }

  /** Where a card dragged out of the window would land on the board, or null when it isn't over the board. */
  function landing(drag: OutgoingCard): Point | null {
    const at = drag.outside ? boardPoint(drag.client) : null
    return at ? snapPoint({ x: at.x - drag.grab.x, y: at.y - drag.grab.y }) : null
  }

  return {
    open,
    setOpen,
    ref,
    preview,
    setPreview,
    outgoing,
    setOutgoing,
    busy,
    setBusy,
    putBack,
    purge,
    empty,
    landing,
    /** Releases a card dragged out of the window, into the bucket under the pointer; false when it went nowhere. */
    dropOut(drag: OutgoingCard): boolean {
      const at = landing(drag)
      const pointer = boardPoint(drag.client)
      if (at && pointer) putBack(drag.card, { ...at, ...dropBucket(pointer, { ...at, width: CARD_WIDTH, height: CARD_HEIGHT }) })
      return at !== null
    },
    close() {
      setOpen(false)
      setPreview(null)
    },
  }
}

export type TrashWindowState = ReturnType<typeof useTrashWindow>
