import { flushSync } from 'react-dom'
import { createRoot } from 'react-dom/client'
import type { LabelBlock, LabelDetail, LabelSize } from '../api/types'
import { LabelText } from './LabelText'
import { labelPixels } from './labels'
import type { Size } from './layout'

/**
 * The size of a label's text, measured the only way there is: laid out, out of sight, in `parent` (the canvas, for its
 * font). The server keeps it, for bucket joins and hit-testing; nothing here reads the page for those.
 */
export function measureLabel(parent: HTMLElement, content: LabelBlock[], size: LabelSize, { wrapWidth, minHeight }: Pick<LabelDetail, 'wrapWidth' | 'minHeight'>): Size {
  const el = document.createElement('div')
  el.className = `label${wrapWidth ? ' wraps' : ''}`
  el.style.cssText = `visibility: hidden; font-size: ${labelPixels[size]}px${wrapWidth ? `; width: ${wrapWidth}px` : ''}${minHeight ? `; min-height: ${minHeight}px` : ''}`
  parent.appendChild(el)
  const root = createRoot(el)
  flushSync(() => root.render(<LabelText content={content} />))
  const measured = { width: Math.ceil(el.offsetWidth), height: Math.ceil(el.offsetHeight) }
  root.unmount()
  el.remove()
  return measured
}
