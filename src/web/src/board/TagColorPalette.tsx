import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { tagColors, type TagColor } from '../api/types'

interface TagColorPaletteProps {
  /** Screen rectangle of the colour circle that opened it; it opens below (or above) it, right-aligned. */
  anchor: DOMRect
  tagId: string
  tagName: string
  value: TagColor
  /** The colour under the pointer, shown on the tag everywhere before it is picked; null when the pointer leaves. */
  onPreview: (color: TagColor | null) => void
  onPick: (color: TagColor) => void
  onClose: () => void
}

/**
 * The 12 tag colours, styled like the tool rail's fill flyout. It is fixed to the screen, so the scrolling list it
 * opens from doesn't clip it. Escape closes only the palette, not the window around it; so does a press elsewhere.
 * Its swatches don't take focus, so typing still goes to the window's search field.
 */
export function TagColorPalette({ anchor, tagId, tagName, value, onPreview, onPick, onClose }: TagColorPaletteProps) {
  const panel = useRef<HTMLDivElement>(null)
  const [place, setPlace] = useState<{ top: number; left: number } | null>(null)

  useLayoutEffect(() => {
    const width = panel.current?.offsetWidth ?? 0
    const height = panel.current?.offsetHeight ?? 0
    const below = anchor.bottom + 6 + height <= window.innerHeight - 8
    setPlace({
      top: below ? anchor.bottom + 6 : Math.max(8, anchor.top - 6 - height),
      left: Math.min(Math.max(8, anchor.right - width), window.innerWidth - width - 8),
    })
  }, [anchor])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key !== 'Escape') return
      // Before the window's own handler, which would close the whole window.
      e.stopPropagation()
      onClose()
    }
    function onPointerDown(e: PointerEvent) {
      // The circle that opened it toggles it itself.
      const toggle = (e.target as Element).closest?.(`[data-tag-color="${tagId}"]`)
      if (!panel.current?.contains(e.target as Node) && !toggle) onClose()
    }
    function onScroll(e: Event) {
      if (!panel.current?.contains(e.target as Node)) onClose()
    }
    document.addEventListener('keydown', onKeyDown, true)
    document.addEventListener('pointerdown', onPointerDown, true)
    document.addEventListener('scroll', onScroll, true)
    return () => {
      document.removeEventListener('keydown', onKeyDown, true)
      document.removeEventListener('pointerdown', onPointerDown, true)
      document.removeEventListener('scroll', onScroll, true)
    }
  }, [tagId, onClose])

  // Whatever closes the palette ends the preview.
  const preview = useRef(onPreview)
  useEffect(() => {
    preview.current = onPreview
  })
  useEffect(() => () => preview.current(null), [])

  return (
    <div
      ref={panel}
      className="tag-palette"
      role="group"
      aria-label={`Colour of ${tagName}`}
      style={place ?? { visibility: 'hidden' }}
      onPointerLeave={() => onPreview(null)}
    >
      {tagColors.map((color) => (
        <button
          key={color}
          type="button"
          className={`swatch tag-swatch tag-color-${color.toLowerCase()}`}
          tabIndex={-1}
          title={color === 'Default' ? 'Default: a shade of the post-it' : color}
          aria-label={color}
          aria-pressed={color === value}
          onPointerDown={(e) => e.preventDefault()}
          onPointerEnter={() => onPreview(color)}
          onClick={() => onPick(color)}
        />
      ))}
    </div>
  )
}
