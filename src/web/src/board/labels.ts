import { useEffect, useState } from 'react'
import { cardRoute, cardPath } from '../api/cardLinks'
import { inkColors, labelSizes, type InkColor, type LabelBlock, type LabelRun, type LabelSize } from '../api/types'
import { inkColor } from './drawings'
import type { Size } from './layout'

/** The font size of a label size, in canvas pixels (BoardLabel.Pixels on the server). */
export const labelPixels: Record<LabelSize, number> = { S: 16, M: 24, L: 40, XL: 64 }

/** What can be switched on and off for a label's text. */
export type LabelFormat = 'bold' | 'italic' | 'underline' | 'strike'
export type LabelListKind = 'Bullet' | 'Numbered'

/** What the toolbar asks of the editor (`LabelEditor`), which is loaded only when someone edits a label. */
export interface LabelEditorHandle {
  blocks: () => LabelBlock[]
  focus: () => void
  selectAll: () => void
  hasSelection: () => boolean
  isActive: (format: LabelFormat) => boolean
  toggle: (format: LabelFormat) => void
  listActive: (kind: LabelListKind) => boolean
  toggleList: (kind: LabelListKind) => void
  setInk: (ink: InkColor) => void
  setSize: (size: LabelSize) => void
  linkHref: () => string | null
  setLink: (url: string) => boolean
  unsetLink: () => void
}

/** The links a label can have, as the server's CardLinks takes them: absolute http(s) URLs, or a card's path. */
export function isLabelLink(url: string) {
  if (cardRoute(url, window.location.origin)) return true
  try {
    const { protocol } = new URL(url)
    return protocol === 'http:' || protocol === 'https:'
  } catch {
    return false
  }
}

/** Follows a label's link: a card opens in place, anything else in a new tab. */
export function followLabelLink(href: string) {
  const route = cardRoute(href, window.location.origin)
  if (route) window.location.hash = cardPath(route).slice(1)
  else window.open(href, '_blank', 'noopener')
}

export const isBlankLabel = (blocks: LabelBlock[]) => blocks.every((b) => b.runs.every((r) => !r.text.trim()))

/** Whether all of a label's text has a format. */
export const allHave = (blocks: LabelBlock[], format: LabelFormat) => blocks.every((b) => b.runs.every((r) => r[format] === true))

/** A label's text with a format switched on or off for all of it. */
export function toggleAll(blocks: LabelBlock[], format: LabelFormat): LabelBlock[] {
  const on = !allHave(blocks, format)
  return blocks.map((b) => ({ ...b, runs: b.runs.map((r): LabelRun => ({ ...r, [format]: on ? true : undefined })) }))
}

/** The style of a run's text, for what it sets itself. */
export const runStyle = (run: LabelRun) => ({
  fontWeight: run.bold ? 700 : undefined,
  fontStyle: run.italic ? 'italic' : undefined,
  textDecorationLine: [run.underline && 'underline', run.strike && 'line-through'].filter(Boolean).join(' ') || undefined,
  color: run.ink ? inkColor[run.ink] : undefined,
  fontSize: run.size ? labelPixels[run.size] : undefined,
})

/**
 * Neighboring blocks of one kind of list as one list: what `LabelText` draws. Each entry is a paragraph, or the
 * items of a list.
 */
export function groupedBlocks(blocks: LabelBlock[]) {
  const groups: { kind: LabelBlock['kind']; blocks: LabelBlock[] }[] = []
  for (const block of blocks) {
    const last = groups.at(-1)
    if (last && block.kind !== 'Paragraph' && last.kind === block.kind) last.blocks.push(block)
    else groups.push({ kind: block.kind, blocks: [block] })
  }
  return groups
}

/**
 * The size of a label's text, measured the only way there is: laid out, out of sight, in `parent` (the canvas, for its
 * font). The server keeps it, for bucket joins and hit-testing; nothing here reads the page for those.
 */
export function measureLabel(parent: HTMLElement, content: LabelBlock[], size: LabelSize): Size {
  const el = document.createElement('div')
  el.className = 'label'
  el.style.cssText = `visibility: hidden; font-size: ${labelPixels[size]}px`
  const fill = (into: HTMLElement, block: LabelBlock) => {
    for (const run of block.runs) {
      const span = document.createElement('span')
      span.textContent = run.text
      const { fontWeight, fontStyle, textDecorationLine, fontSize } = runStyle(run)
      if (fontWeight) span.style.fontWeight = String(fontWeight)
      if (fontStyle) span.style.fontStyle = fontStyle
      if (textDecorationLine) span.style.textDecorationLine = textDecorationLine
      if (fontSize) span.style.fontSize = `${fontSize}px`
      into.appendChild(span)
    }
    if (block.runs.length === 0) into.appendChild(document.createElement('br'))
  }
  for (const group of groupedBlocks(content)) {
    if (group.kind === 'Paragraph') {
      const p = document.createElement('p')
      fill(p, group.blocks[0])
      el.appendChild(p)
    } else {
      const list = document.createElement(group.kind === 'Bullet' ? 'ul' : 'ol')
      for (const block of group.blocks) {
        const item = document.createElement('li')
        fill(item, block)
        list.appendChild(item)
      }
      el.appendChild(list)
    }
  }
  parent.appendChild(el)
  const measured = { width: Math.ceil(el.offsetWidth), height: Math.ceil(el.offsetHeight) }
  el.remove()
  return measured
}

/** The ink and size new labels get, kept for the next visit. */
export function useLabelStyle(key: string) {
  const [style, setStyle] = useState<{ ink: InkColor; size: LabelSize }>(() => {
    try {
      const value = JSON.parse(localStorage.getItem(key) ?? 'null') as { ink?: unknown; size?: unknown } | null
      return { ink: inkColors.find((c) => c === value?.ink) ?? 'Chalk', size: labelSizes.find((s) => s === value?.size) ?? 'M' }
    } catch {
      return { ink: 'Chalk', size: 'M' }
    }
  })
  useEffect(() => {
    try {
      localStorage.setItem(key, JSON.stringify(style))
    } catch {
      // Private windows may refuse: the choice only lasts this visit then.
    }
  }, [key, style])
  return { ...style, setInk: (ink: InkColor) => setStyle((s) => ({ ...s, ink })), setSize: (size: LabelSize) => setStyle((s) => ({ ...s, size })) }
}

export const LABEL_BOARD_STYLE = 'prikboard.label'
export const LABEL_CARD_STYLE = 'prikboard.cardLabel'
