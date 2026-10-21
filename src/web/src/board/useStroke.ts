// The pen stroke being drawn on a canvas (the board, or a card's), shared by both.
import { useRef, useState, type PointerEvent } from 'react'
import { api } from '../api/client'
import type { InkColor, Point, StrokePoint } from '../api/types'
import { addPoint, pressureOf, rememberPoints, strokeToDrawing } from './drawings'
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
 * Collects a stroke from the pointer and saves it as a drawing when the pen comes up. `toCanvas` turns a screen point into
 * the canvas's own pixels.
 */
export function useStroke(toCanvas: (e: { clientX: number; clientY: number }) => Point) {
  const [stroke, setState] = useState<Stroke | null>(null)
  /**
   * The stroke as it is right now. Pen events can come faster than renders, so the handlers read and write this rather
   * than the state, which only draws it.
   */
  const live = useRef<Stroke | null>(null)
  const set = (next: Stroke | null | ((s: Stroke | null) => Stroke | null)) => {
    live.current = typeof next === 'function' ? next(live.current) : next
    setState(live.current)
  }

  return {
    /** The stroke to draw: being drawn, or finished and being saved. */
    stroke,
    /** Whether the pen is down right now. */
    drawing: () => !!live.current && !live.current.done,
    start(e: PointerAt, color: InkColor, width: number) {
      set({ points: addPoint([], toCanvas(e), pressureOf(e)), color, width })
    },
    /** Adds every point the pointer went through since the last event, for a smooth line. */
    extend(e: PointerEvent) {
      const current = live.current
      if (!current || current.done) return
      const events: PointerAt[] = e.nativeEvent.getCoalescedEvents?.() ?? []
      let points = current.points
      for (const ev of events.length > 0 ? events : [e.nativeEvent]) points = addPoint(points, toCanvas(ev), pressureOf(ev))
      if (points !== current.points) set({ ...current, points })
    },
    /** Drops a stroke the pen didn't finish (the pointer was taken away). */
    cancel() {
      set((s) => (s && !s.done ? null : s))
    },
    /**
     * Saves the stroke as a drawing on the board, or on a card's canvas, as one undo step. It stays on screen until the
     * board has reloaded with it. The last move has the end of the line already (a pen lifting off reports no pressure).
     */
    finish(run: RecordingRun, boardId: string, cardId?: string) {
      const current = live.current
      if (!current || current.done) return
      const done = { ...current, done: true }
      set(done)
      const { x, y, points } = strokeToDrawing(done.points, done.width, cardId ? 0 : MIN_COORDINATE)
      void run(
        async () => {
          const drawing = await api.createDrawing(boardId, { x, y, points, color: done.color, strokeWidth: done.width, cardId })
          rememberPoints(drawing.id, points)
          return drawing
        },
        (drawing) => step('Draw', [created(target.drawing(drawing.id), snap.drawing(drawing))]),
      ).then(() => set((s) => (s === done ? null : s)))
    },
  }
}
