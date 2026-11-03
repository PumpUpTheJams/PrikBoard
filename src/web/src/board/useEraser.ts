// The eraser rubbing out parts of drawings on a canvas (the board, or a card's), shared by both.
import { useRef, useState, type PointerEvent } from 'react'
import { api } from '../api/client'
import type { DrawingDetail, Point } from '../api/types'
import { eraseAlong, erasedDrawing, loadedStrokes, rememberStrokes, type InkedStrokes } from './drawings'
import { runAll } from './ops'
import { deleted, snap, target, type RecordingRun } from './undo'

/** A pointer position, as pointer events and the coalesced events in them have it. */
type PointerAt = { clientX: number; clientY: number }

/** A drawing the eraser has touched in this gesture: as it was (`before`), and what is left of it. */
interface Touched {
  drawing: DrawingDetail
  before: InkedStrokes
  left: InkedStrokes
}

/** How far past a drawing's box the thickest line can reach (half of 8 px at full pressure, rounded up). */
const LINE_REACH = 7

/**
 * Rubs out what the pointer passes over in `drawings()` (those on this canvas) while it is pressed. What is erased goes
 * at once on screen (`erased`, by drawing id, for DrawingView); on release each touched drawing is saved (what is left,
 * or deleted when nothing is) as one undo step. `toCanvas` turns a screen point into the canvas's own pixels.
 */
export function useEraser(toCanvas: (e: PointerAt) => Point, drawings: () => readonly DrawingDetail[]) {
  /** What is left of drawings being erased or saved, until the board has them. */
  const [erased, setErased] = useState<ReadonlyMap<string, InkedStrokes>>(() => new Map())
  /** The drawings touched in the gesture under way, and where the eraser was last; null when it isn't pressed. */
  const gesture = useRef<{ touched: Map<string, Touched>; last: Point; radius: number } | null>(null)
  /** Drawings whose erasing is still being saved: left alone until the board has them, so two saves can't cross. */
  const saving = useRef(new Set<string>())

  function rub(to: Point) {
    const g = gesture.current
    if (!g) return
    const from = g.last
    g.last = to
    const reach = g.radius + LINE_REACH
    const left = Math.min(from.x, to.x) - reach
    const right = Math.max(from.x, to.x) + reach
    const top = Math.min(from.y, to.y) - reach
    const bottom = Math.max(from.y, to.y) + reach
    const changed: Touched[] = []
    for (const d of drawings()) {
      if (saving.current.has(d.id)) continue
      const known = g.touched.get(d.id)
      const drawing = known?.drawing ?? d
      if (drawing.x > right || drawing.x + drawing.width < left || drawing.y > bottom || drawing.y + drawing.height < top) continue
      let current = known?.left
      if (!current) {
        // Drawings on screen have their strokes; one that isn't loaded yet can't be under the pointer anyway.
        const strokes = loadedStrokes(drawing)
        if (!strokes) continue
        current = { strokes, colors: drawing.colors }
      }
      const at = { x: drawing.x, y: drawing.y }
      const rest = eraseAlong(current, { x: from.x - at.x, y: from.y - at.y }, { x: to.x - at.x, y: to.y - at.y }, g.radius)
      if (rest === current) continue
      const touched = { drawing, before: known?.before ?? current, left: rest }
      g.touched.set(d.id, touched)
      changed.push(touched)
    }
    if (changed.length > 0) {
      setErased((m) => {
        const next = new Map(m)
        for (const t of changed) next.set(t.drawing.id, t.left)
        return next
      })
    }
  }

  /** Shows the touched drawings as the board has them again. */
  const forget = (ids: string[]) =>
    setErased((m) => {
      const next = new Map(m)
      for (const id of ids) next.delete(id)
      return next
    })

  return {
    /** What is left of drawings being erased, by id: DrawingView shows these instead of their strokes. */
    erased,
    /** Whether the eraser is pressed right now. */
    erasing: () => gesture.current !== null,
    /** Presses the eraser, of `radius` canvas px, at the pointer. */
    start(e: PointerAt, radius: number) {
      const at = toCanvas(e)
      gesture.current = { touched: new Map(), last: at, radius }
      rub(at)
    },
    /** Rubs along every point the pointer went through since the last event. */
    extend(e: PointerEvent) {
      if (!gesture.current) return
      const events: PointerAt[] = e.nativeEvent.getCoalescedEvents?.() ?? []
      for (const ev of events.length > 0 ? events : [e.nativeEvent]) rub(toCanvas(ev))
    },
    /** Puts back what the gesture erased without saving it (the pointer was taken away). */
    cancel() {
      const g = gesture.current
      gesture.current = null
      if (g) forget([...g.touched.keys()])
    },
    /**
     * Saves what the gesture erased as one undo step, "Erase": what is left of each drawing it touched (setDrawingStrokes,
     * with the corner moved in as far as the strokes left allow), or the drawing deleted when nothing is left.
     */
    finish(run: RecordingRun) {
      const g = gesture.current
      gesture.current = null
      if (!g || g.touched.size === 0) return
      const done = [...g.touched.values()]
      const ids = done.map((t) => t.drawing.id)
      for (const id of ids) saving.current.add(id)
      void runAll(run, 'Erase', undefined, async (changes) => {
        for (const { drawing, before, left } of done) {
          const rest = erasedDrawing(drawing, left)
          if (!rest) {
            await api.deleteDrawing(drawing.id)
            changes.push(deleted(target.drawing(drawing.id), snap.drawing(drawing)))
            continue
          }
          const strokes = rest.strokes.map((s, i) => ({ ...s, color: rest.colors[i] }))
          const after = await api.setDrawingStrokes(drawing.id, { x: rest.x, y: rest.y, strokes })
          // Known here, so the board shows them without fetching, and undo can compare them.
          rememberStrokes(after, rest.strokes)
          changes.push({ target: target.shape(drawing.id), before: snap.shape(drawing, before.strokes), after: snap.shape(after, rest.strokes) })
        }
      }).finally(() => {
        for (const id of ids) saving.current.delete(id)
        forget(ids)
      })
    },
  }
}
