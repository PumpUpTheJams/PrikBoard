import { api, ApiError } from '../api/client'
import type { BoardClip, ClippedImage, CopyItemsRequest, PastedItems, Point, Rect } from '../api/types'
import { CARD_HEIGHT, CARD_WIDTH, snap } from './layout'

// Copying and pasting many items at once, up to a whole board. A copy fetches the clip without its images' data and then
// each image's file, and a big paste checks the clip, uploads its images one by one and only then pastes it, so both can
// show how far they are and be cancelled.

/** How far a copy or a paste has come. */
export interface TransferProgress {
  step: 'copying' | 'checking' | 'uploading' | 'saving'
  /** Images done, and how many there are. */
  images: number
  imagesTotal: number
  /** Bytes of the images done (downloaded for a copy, uploaded for a paste), and in all when known. */
  bytes: number
  bytesTotal: number | null
}

/** How many images are fetched or uploaded at the same time. */
const PARALLEL = 4

/** A paste is made in steps, with a preview of where it goes first, from this many characters of clip. */
const BIG_CLIP_CHARS = 5 * 1024 * 1024

/** A clip with more items than this is pasted in steps too. */
const BIG_CLIP_ITEMS = 50

/** Whether a clip `size` characters long is pasted in steps (placed first, then uploaded with progress), not at once. */
export const isBigClip = (clip: BoardClip, size: number) =>
  size > BIG_CLIP_CHARS ||
  (clip.buckets?.length ?? 0) > 0 ||
  clip.cards.length + clip.images.filter((i) => !i.cardId).length + clip.drawings.filter((d) => !d.cardId).length > BIG_CLIP_ITEMS

/** Runs `work` on every item, PARALLEL at a time; the first failure (or an abort) stops the rest. */
async function inParallel<T>(items: T[], work: (item: T, index: number) => Promise<void>, signal: AbortSignal) {
  let next = 0
  async function worker() {
    while (next < items.length) {
      signal.throwIfAborted()
      const index = next++
      await work(items[index], index)
    }
  }
  await Promise.all(Array.from({ length: Math.min(PARALLEL, items.length) }, worker))
}

/** How often an upload is tried again after the connection failed. */
const RETRIES = 2

/**
 * Runs `send`, again after a failed connection (one of a hundred uploads may well meet one) or an answer the server
 * couldn't give; uploading the same file twice is harmless, as only the paste uses it.
 */
async function withRetries<T>(send: () => Promise<T>, signal: AbortSignal): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await send()
    } catch (error) {
      const retryable = error instanceof ApiError && (error.status === 0 || error.status >= 500)
      if (!retryable || attempt >= RETRIES || signal.aborted) throw error
      await new Promise((resolve) => setTimeout(resolve, 500 * (attempt + 1)))
    }
  }
}

/** A file as base64, without the data URL's prefix. */
function base64(blob: Blob) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve((reader.result as string).slice((reader.result as string).indexOf(',') + 1))
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(blob)
  })
}

/** An image's file from the base64 in a clip. */
function fileOf(image: ClippedImage) {
  const binary = atob(image.data)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return new Blob([bytes], { type: 'application/octet-stream' })
}

/** About how many bytes the base64 in a clip holds. */
const bytesIn = (image: ClippedImage) => Math.floor((image.data.length * 3) / 4)

/** The clip of `what` with everything on it, its images fetched one by one so `onProgress` can follow them. */
export async function loadClip(
  boardId: string,
  what: CopyItemsRequest,
  onProgress: (progress: TransferProgress) => void,
  signal: AbortSignal,
): Promise<BoardClip> {
  const clip = await api.copyItems(boardId, { ...what, imageData: false }, signal)
  const progress: TransferProgress = { step: 'copying', images: 0, imagesTotal: clip.images.length, bytes: 0, bytesTotal: null }
  onProgress({ ...progress })
  await inParallel(
    clip.images,
    async (image) => {
      if (image.id && !image.data) {
        const file = await api.getImageContent(image.id, signal)
        image.data = await base64(file)
        progress.bytes += file.size
      }
      progress.images++
      onProgress({ ...progress })
    },
    signal,
  )
  return clip
}

/**
 * Pastes a big clip with its top-left at `at`: checks it first, then uploads its images, and then calls `paste` with the
 * clip without the images' data and the uploads' tokens, which it can't take back. `onProgress` follows each step.
 */
export async function pasteInSteps(
  boardId: string,
  clip: BoardClip,
  at: Point,
  onProgress: (progress: TransferProgress) => void,
  signal: AbortSignal,
  paste: (clip: BoardClip, imageFiles: (string | null)[]) => Promise<PastedItems | undefined>,
) {
  const bare: BoardClip = { ...clip, images: clip.images.map((i) => ({ ...i, data: '' })) }
  const progress: TransferProgress = {
    step: 'checking',
    images: 0,
    imagesTotal: clip.images.length,
    bytes: 0,
    bytesTotal: clip.images.reduce((sum, i) => sum + bytesIn(i), 0),
  }
  onProgress({ ...progress })
  // Refused before anything is uploaded, if it would be.
  await api.checkPaste(boardId, bare, at, undefined, signal)

  progress.step = 'uploading'
  onProgress({ ...progress })
  const tokens: (string | null)[] = clip.images.map(() => null)
  // Counted image by image as each is done: an image is at most 10 MB, and several go up at once.
  await inParallel(
    clip.images,
    async (image, index) => {
      if (!image.data) return
      const file = fileOf(image)
      const uploaded = await withRetries(() => api.uploadPasteImage(boardId, file, signal), signal)
      tokens[index] = uploaded.token
      progress.images++
      progress.bytes += file.size
      onProgress({ ...progress })
    },
    signal,
  )

  signal.throwIfAborted()
  progress.step = 'saving'
  onProgress({ ...progress })
  return paste(bare, tokens)
}

/** The outlines a clip is previewed with, relative to its top-left: its buckets, and its cards, images and drawings on the board. */
export interface ClipShape {
  width: number
  height: number
  buckets: Rect[]
  cards: Rect[]
  others: Rect[]
}

/** A clip's outline as it will be pasted: places snapped to the grid, the top-left at 0, 0, as pasteItems moves them. */
export function clipShape(clip: BoardClip): ClipShape {
  const buckets = (clip.buckets ?? []).map((b) => ({ x: snap(b.x), y: snap(b.y), width: b.width, height: b.height }))
  const cards = clip.cards.map((n) => ({ x: snap(n.x), y: snap(n.y), width: CARD_WIDTH, height: CARD_HEIGHT }))
  const others = [
    ...clip.images.filter((i) => !i.cardId).map((i) => ({ x: snap(i.x), y: snap(i.y), width: i.width, height: i.height ?? i.width })),
    ...clip.drawings
      .filter((d) => !d.cardId)
      .map((d) => {
        const points = (d.strokes ?? []).flatMap((s) => s.points.map(([x, y]) => ({ x: x + s.strokeWidth, y: y + s.strokeWidth })))
        return {
          x: snap(d.x),
          y: snap(d.y),
          width: Math.max(1, ...points.map((p) => p.x)),
          height: Math.max(1, ...points.map((p) => p.y)),
        }
      }),
  ]
  const all = [...buckets, ...cards, ...others]
  const left = Math.min(...all.map((r) => r.x))
  const top = Math.min(...all.map((r) => r.y))
  const moved = (r: Rect) => ({ ...r, x: r.x - left, y: r.y - top })
  return {
    width: Math.max(...all.map((r) => r.x + r.width)) - left,
    height: Math.max(...all.map((r) => r.y + r.height)) - top,
    buckets: buckets.map(moved),
    cards: cards.map(moved),
    others: others.map(moved),
  }
}

/** What a copy or paste is doing, in words. */
export function progressText(progress: TransferProgress | null, what: string) {
  if (!progress) return `Copying ${what}…`
  const images = `${progress.images.toLocaleString()} of ${progress.imagesTotal.toLocaleString()} images`
  switch (progress.step) {
    case 'copying':
      return progress.imagesTotal > 0 ? `Copying ${what}: ${images}, ${sizeText(progress.bytes)}` : `Copying ${what}…`
    case 'checking':
      return `Checking ${what}…`
    case 'uploading':
      return progress.imagesTotal > 0
        ? `Uploading ${images}, ${sizeText(progress.bytes)} of ${sizeText(progress.bytesTotal ?? 0)}`
        : `Uploading ${what}…`
    case 'saving':
      return `Adding ${what} to the board…`
  }
}

/** "84 MB", "320 KB": a size for people. */
export function sizeText(bytes: number) {
  if (bytes >= 1024 * 1024) return `${Math.round((bytes / 1024 / 1024) * 10) / 10} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}
