// The pen strokes being drawn on a canvas (the board, or a card's), shared by both.
import { useEffect, useRef, useState, type PointerEvent } from 'react'
import { ApiError, api } from '../api/client'
import type { DrawingDetail, InkColor, NewDrawingStroke, Point, StrokePoint } from '../api/types'
import { addPoint, loadDrawingStrokes, pressureOf, rememberStrokes, strokeToDrawing } from './drawings'
import { MIN_COORDINATE } from './layout'
import { created, snap, step, target, type RecordingRun } from './undo'

/** A pen stroke being drawn: its points on the canvas. A finished one (`done`) shows until the board has it. */
export interface Stroke {
  points: StrokePoint[]
  color: InkColor
  width: number
  done?: boolean
}

/** A pointer position, as pointer events and the coalesced events in them have it. */
type PointerAt = { clientX: number; clientY: number; pointerType: string; pressure: number }

/**
 * The drawing a pen session is adding to: everything drawn between picking up the pen and putting it down is one
 * drawing. `drawing` settles to the drawing as the last saved stroke left it (null until the first one is saved).
 */
interface Session {
  /** The canvas it draws on: a card's id, or the board. */
  canvas: string
  drawing: Promise<DrawingDetail | null>
}

/**
 * Collects strokes from the pointer and saves them while the pen is `active`: the first as a new drawing, the rest added
 * to it, so a sketch selects, moves and deletes as one. Putting the pen down (`active` false) ends the drawing; the next
 * stroke starts a new one. `toCanvas` turns a screen point into the canvas's own pixels.
 */
export function useStroke(toCanvas: (e: { clientX: number; clientY: number }) => Point, active: boolean) {
  /** The stroke being drawn (last), after the finished ones still being saved. */
  const [strokes, setState] = useState<Stroke[]>([])
  /**
   * The strokes as they are right now. Pen events can come faster than renders, so the handlers read and write this
   * rather than the state, which only draws them.
   */
  const live = useRef<Stroke[]>([])
  const set = (next: (s: Stroke[]) => Stroke[]) => {
    live.current = next(live.current)
    setState(live.current)
  }
  const current = () => {
    const last = live.current.at(-1)
    return last && !last.done ? last : null
  }

  const session = useRef<Session | null>(null)
  useEffect(() => {
    if (!active) session.current = null
  }, [active])

  return {
    /** The strokes to draw: the one being drawn, and finished ones being saved. */
    strokes,
    /** Whether the pen is down right now. */
    drawing: () => current() !== null,
    start(e: PointerAt, color: InkColor, width: number) {
      const stroke = { points: addPoint([], toCanvas(e), pressureOf(e)), color, width }
      set((s) => [...s.filter((x) => x.done), stroke])
    },
    /** Adds every point the pointer went through since the last event, for a smooth line. */
    extend(e: PointerEvent) {
      const stroke = current()
      if (!stroke) return
      const events: PointerAt[] = e.nativeEvent.getCoalescedEvents?.() ?? []
      let points = stroke.points
      for (const ev of events.length > 0 ? events : [e.nativeEvent]) points = addPoint(points, toCanvas(ev), pressureOf(ev))
      if (points !== stroke.points) set((s) => s.map((x) => (x === stroke ? { ...stroke, points } : x)))
    },
    /** Drops a stroke the pen didn't finish (the pointer was taken away). */
    cancel() {
      set((s) => s.filter((x) => x.done))
    },
    /**
     * Saves the stroke, as one undo step: added to this pen session's drawing, or as a new drawing on the board, or on a
     * card's canvas, when the session has none yet (or it is gone, or full). It stays on screen until the board has
     * reloaded with it. The last move has the end of the line already (a pen lifting off reports no pressure).
     */
    finish(run: RecordingRun, boardId: string, cardId?: string) {
      const stroke = current()
      if (!stroke) return
      const done = { ...stroke, done: true }
      set((s) => s.map((x) => (x === stroke ? done : x)))
      const { x, y, points } = strokeToDrawing(done.points, done.width, cardId ? 0 : MIN_COORDINATE)
      const sent: NewDrawingStroke = { x, y, points, color: done.color, strokeWidth: done.width }

      const canvas = cardId ?? 'board'
      const before = session.current?.canvas === canvas ? session.current.drawing : Promise.resolve(null)
      const drawing = before.then(async (existing) => {
        let saved = existing
        // Strokes are saved one after another, so each knows the drawing the one before it made or added to.
        await run(
          async () => {
            if (existing && existing.cardId === (cardId ?? null)) {
              try {
                const after = await api.addDrawingStroke(existing.id, sent)
                // Its new strokes are in before the board reloads, so it doesn't blink.
                await loadDrawingStrokes(after)
                saved = after
                return { before: existing, after }
              } catch (error) {
                // Gone (deleted, or undone) or full: this stroke starts a new drawing.
                if (!(error instanceof ApiError && (error.status === 404 || error.status === 409))) throw error
              }
            }
            const after = await api.createDrawing(boardId, { ...sent, cardId })
            rememberStrokes(after, [{ strokeWidth: done.width, points }])
            saved = after
            return { before: null, after }
          },
          ({ before, after }) => {
            if (!before) return step('Draw', [created(target.drawing(after.id), snap.drawing(after))])
            const index = after.colors.length - 1
            // Measured from the drawing's corner before, so a redo puts it back on the drawing wherever that is then.
            const relative = { ...sent, x: sent.x - before.x, y: sent.y - before.y }
            return step('Draw', [created(target.stroke(after.id, index, relative), snap.stroke(after, index)!)])
          },
        )
        return saved
      })
      if (active) session.current = { canvas, drawing }
      void drawing.then(() => set((s) => s.filter((x) => x !== done)))
    },
  }
}
