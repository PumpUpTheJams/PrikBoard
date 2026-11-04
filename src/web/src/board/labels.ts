import { useEffect, useState } from 'react'
import { cardRoute, cardPath } from '../api/cardLinks'
import { labelInks, labelSizes, type InkColor, type LabelBlock, type LabelRun, type LabelSize } from '../api/types'
import { inkColor } from './drawings'

/** The font size of a label size, in canvas pixels (BoardLabel.Pixels on the server). */
export const labelPixels: Record<LabelSize, number> = { S: 14, M: 21, L: 35, XL: 56 }

/** What can be switched on and off for a label's text. */
export type LabelFormat = 'bold' | 'italic' | 'underline' | 'strike' | 'code'
export type LabelListKind = 'Bullet' | 'Numbered'
/** The kinds of block the toolbar turns text into while editing. */
export type LabelBlockTool = 'Heading' | 'Task' | 'Quote' | 'Code' | 'Table'

/** What text wraps at when nobody chose a width: the mobile layout's new labels. BoardLabel.DefaultWrapWidth on the server. */
export const DEFAULT_WRAP_WIDTH = 340

/** What the toolbar asks of the editor (`LabelEditor`), which is loaded only when someone edits a label. */
export interface LabelEditorHandle {
  blocks: () => LabelBlock[]
  focus: () => void
  isEmpty: () => boolean
  /** The ink and size of the text selected, or of what is typed at the cursor; null where the selected text has several. */
  styleHere: (base: { ink: InkColor; size: LabelSize }) => { ink: InkColor | null; size: LabelSize | null }
  isActive: (format: LabelFormat) => boolean
  toggle: (format: LabelFormat) => void
  listActive: (kind: LabelListKind) => boolean
  toggleList: (kind: LabelListKind) => void
  blockActive: (kind: LabelBlockTool) => boolean
  toggleBlock: (kind: LabelBlockTool) => void
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

/** Every run of text in a block, those in table cells too. */
export const runsOf = (block: LabelBlock) => [...block.runs, ...(block.cells ?? []).flat()]

export const isBlankLabel = (blocks: LabelBlock[]) => blocks.every((b) => b.kind !== 'Rule' && runsOf(b).every((r) => !r.text.trim()))

/** Whether two labels' content is the same, whichever order their fields are in. */
export function sameContent(a: LabelBlock[], b: LabelBlock[]) {
  const canonical = (blocks: LabelBlock[]) =>
    JSON.stringify(blocks, (_, value: unknown) =>
      value && typeof value === 'object' && !Array.isArray(value)
        ? Object.fromEntries(Object.entries(value).filter(([, v]) => v !== null && v !== undefined).sort(([x], [y]) => (x < y ? -1 : 1)))
        : value,
    )
  return canonical(a) === canonical(b)
}

/** The style of a run's text, for what it sets itself. */
export const runStyle = (run: LabelRun) => ({
  fontWeight: run.bold ? 700 : undefined,
  fontStyle: run.italic ? 'italic' : undefined,
  textDecorationLine: [run.underline && 'underline', run.strike && 'line-through'].filter(Boolean).join(' ') || undefined,
  color: run.ink ? inkColor[run.ink] : undefined,
  fontSize: run.size ? labelPixels[run.size] : undefined,
})

/** A block is a list item. */
export const isListItem = (block: LabelBlock) => block.kind === 'Bullet' || block.kind === 'Numbered' || block.kind === 'Task'

/**
 * Neighboring blocks that are drawn as one: what `LabelText` draws. Each entry is one block, or the items of a list
 * (nested ones too), the paragraphs of a quote or the rows of a table.
 */
export function groupedBlocks(blocks: LabelBlock[]) {
  const groups: { kind: 'List' | 'Quote' | 'Table' | 'One'; blocks: LabelBlock[] }[] = []
  for (const block of blocks) {
    const kind = isListItem(block) ? 'List' : block.kind === 'Quote' ? 'Quote' : block.kind === 'TableRow' ? 'Table' : 'One'
    const last = groups.at(-1)
    if (last && kind !== 'One' && last.kind === kind) last.blocks.push(block)
    else groups.push({ kind, blocks: [block] })
  }
  return groups
}

/** Where a label's text starts from its corner: its border and padding (`.label`; BoardLabel.PaddingX and PaddingY on the server). */
export const LABEL_INSET = { x: 15, y: 11 }

/** The ink and size new labels get, kept for the next visit. */
export function useLabelStyle(key: string) {
  const [style, setStyle] = useState<{ ink: InkColor; size: LabelSize }>(() => {
    try {
      const value = JSON.parse(localStorage.getItem(key) ?? 'null') as { ink?: unknown; size?: unknown } | null
      return { ink: labelInks.find((c) => c === value?.ink) ?? 'Theme', size: labelSizes.find((s) => s === value?.size) ?? 'M' }
    } catch {
      return { ink: 'Theme', size: 'M' }
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
