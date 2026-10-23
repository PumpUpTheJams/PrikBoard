import { useLayoutEffect, useState, type DragEvent, type PointerEvent, type RefObject } from 'react'
import type { Rect } from '../api/types'
import { draggingFiles, imageFiles } from './files'

/** A canvas's scroll viewport in screen pixels (its scroll position and size), kept up to date. */
export function useScrollView(scrollRef: RefObject<HTMLDivElement | null>): Rect {
  const [view, setView] = useState<Rect>({ x: 0, y: 0, width: 0, height: 0 })
  useLayoutEffect(() => {
    const el = scrollRef.current!
    const update = () => setView({ x: el.scrollLeft, y: el.scrollTop, width: el.clientWidth, height: el.clientHeight })
    update()
    const observer = new ResizeObserver(update)
    observer.observe(el)
    el.addEventListener('scroll', update, { passive: true })
    return () => {
      observer.disconnect()
      el.removeEventListener('scroll', update)
    }
  }, [scrollRef])
  return view
}

/** A scroll viewport (screen pixels) kept outside React state: read it when needed, or subscribe to its changes. */
export interface ScrollView {
  get: () => Rect
  subscribe: (listener: () => void) => () => void
}

/**
 * Like useScrollView, but a scroll doesn't render the component: only the viewport's size is state (it changes when the
 * window resizes), and where it is scrolled to is in `view`, for what shows it (the minimap's viewport) to subscribe to.
 */
export function useScrollStore(scrollRef: RefObject<HTMLDivElement | null>): { size: { width: number; height: number }; view: ScrollView } {
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [view] = useState(() => {
    let current: Rect = { x: 0, y: 0, width: 0, height: 0 }
    const listeners = new Set<() => void>()
    return {
      get: () => current,
      subscribe(listener: () => void) {
        listeners.add(listener)
        return () => listeners.delete(listener)
      },
      set(next: Rect) {
        if (next.x === current.x && next.y === current.y && next.width === current.width && next.height === current.height) return
        current = next
        for (const listener of listeners) listener()
      },
    }
  })
  useLayoutEffect(() => {
    const el = scrollRef.current!
    const scrolled = () => view.set({ x: el.scrollLeft, y: el.scrollTop, width: el.clientWidth, height: el.clientHeight })
    const resized = () => {
      scrolled()
      setSize((s) => (s.width === el.clientWidth && s.height === el.clientHeight ? s : { width: el.clientWidth, height: el.clientHeight }))
    }
    resized()
    const observer = new ResizeObserver(resized)
    observer.observe(el)
    el.addEventListener('scroll', scrolled, { passive: true })
    return () => {
      observer.disconnect()
      el.removeEventListener('scroll', scrolled)
    }
  }, [scrollRef, view])
  return { size, view }
}

/** A pan in progress: where the pointer went down and the scroll position at that moment, in screen pixels. */
interface Pan {
  x: number
  y: number
  left: number
  top: number
}

/** Panning a canvas by dragging: the canvas holds the pointer, and the scroll viewport follows it. */
export function usePan(scrollRef: RefObject<HTMLDivElement | null>, canvasRef: RefObject<HTMLDivElement | null>) {
  const [pan, setPan] = useState<Pan | null>(null)
  return {
    panning: pan !== null,
    start(e: PointerEvent) {
      const el = scrollRef.current!
      canvasRef.current!.setPointerCapture(e.pointerId)
      setPan({ x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop })
    },
    /** Scrolls along with the pointer; false when no pan is in progress. */
    move(e: PointerEvent): boolean {
      if (!pan) return false
      const el = scrollRef.current!
      el.scrollLeft = pan.left - (e.clientX - pan.x)
      el.scrollTop = pan.top - (e.clientY - pan.y)
      return true
    },
    /** Ends the pan; false when there was none. */
    end(): boolean {
      setPan(null)
      return pan !== null
    },
  }
}

/**
 * Image files dragged over an element and dropped on it (`enabled` false: left to the browser). `over`: files are being
 * dragged over it. With `stop`, the events don't go on to the elements around it.
 */
export function useFileDrop(enabled: boolean, onFiles: (files: File[], e: DragEvent) => void, { stop = false } = {}) {
  const [over, setOver] = useState(false)
  const handlers = {
    onDragOver(e: DragEvent) {
      if (!enabled || !draggingFiles(e.dataTransfer)) return
      e.preventDefault()
      if (stop) e.stopPropagation()
      e.dataTransfer.dropEffect = 'copy'
      setOver(true)
    },
    onDragLeave(e: DragEvent) {
      if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false)
    },
    onDrop(e: DragEvent) {
      if (!enabled || !draggingFiles(e.dataTransfer)) return
      e.preventDefault()
      if (stop) e.stopPropagation()
      setOver(false)
      onFiles(imageFiles(e.dataTransfer), e)
    },
  }
  return { over, handlers }
}
