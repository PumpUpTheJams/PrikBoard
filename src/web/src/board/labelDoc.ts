import { inkColors, labelSizes, type LabelBlock, type LabelRun } from '../api/types'
import { inkColor } from './drawings'
import { isLabelLink, labelPixels } from './labels'

// A label's blocks as the editor's document and back. Only what labels have survives the way back, so whatever got
// into the editor (pasted colors, nested lists) can't reach the server.

interface DocMark {
  type: string
  attrs?: Record<string, unknown>
}

export interface DocNode {
  type: string
  text?: string
  marks?: DocMark[]
  attrs?: Record<string, unknown>
  content?: DocNode[]
}

function marksOf(run: LabelRun): DocMark[] | undefined {
  const marks: DocMark[] = []
  if (run.bold) marks.push({ type: 'bold' })
  if (run.italic) marks.push({ type: 'italic' })
  if (run.underline) marks.push({ type: 'underline' })
  if (run.strike) marks.push({ type: 'strike' })
  if (run.link) marks.push({ type: 'link', attrs: { href: run.link } })
  if (run.ink || run.size) {
    marks.push({ type: 'textStyle', attrs: { color: run.ink ? inkColor[run.ink] : null, fontSize: run.size ? `${labelPixels[run.size]}px` : null } })
  }
  return marks.length > 0 ? marks : undefined
}

function inlineOf(runs: LabelRun[]): DocNode[] {
  const nodes: DocNode[] = []
  for (const run of runs) {
    const marks = marksOf(run)
    run.text.split('\n').forEach((part, i) => {
      if (i > 0) nodes.push({ type: 'hardBreak', ...(marks ? { marks } : {}) })
      if (part) nodes.push({ type: 'text', text: part, ...(marks ? { marks } : {}) })
    })
  }
  return nodes
}

const paragraphOf = (block: LabelBlock): DocNode => {
  const content = inlineOf(block.runs)
  return { type: 'paragraph', ...(content.length > 0 ? { content } : {}) }
}

/** Blocks as a document. */
export function docOf(blocks: LabelBlock[]): DocNode {
  const content: DocNode[] = []
  for (const block of blocks) {
    if (block.kind === 'Paragraph') {
      content.push(paragraphOf(block))
      continue
    }
    const type = block.kind === 'Bullet' ? 'bulletList' : 'orderedList'
    const item: DocNode = { type: 'listItem', content: [paragraphOf(block)] }
    const last = content.at(-1)
    if (last?.type === type) last.content!.push(item)
    else content.push({ type, content: [item] })
  }
  return { type: 'doc', content: content.length > 0 ? content : [{ type: 'paragraph' }] }
}

function runStyleOf(marks: DocMark[] | undefined): Omit<LabelRun, 'text'> {
  const style: Omit<LabelRun, 'text'> = {}
  for (const mark of marks ?? []) {
    if (mark.type === 'bold') style.bold = true
    else if (mark.type === 'italic') style.italic = true
    else if (mark.type === 'underline') style.underline = true
    else if (mark.type === 'strike') style.strike = true
    else if (mark.type === 'link' && typeof mark.attrs?.href === 'string' && isLabelLink(mark.attrs.href)) style.link = mark.attrs.href
    else if (mark.type === 'textStyle') {
      const ink = inkColors.find((c) => inkColor[c] === mark.attrs?.color)
      const size = labelSizes.find((s) => `${labelPixels[s]}px` === mark.attrs?.fontSize)
      if (ink) style.ink = ink
      if (size) style.size = size
    }
  }
  return style
}

function runsOf(nodes: DocNode[] | undefined): LabelRun[] {
  const runs: LabelRun[] = []
  for (const node of nodes ?? []) {
    const text = node.type === 'text' ? (node.text ?? '') : node.type === 'hardBreak' ? '\n' : ''
    if (!text) continue
    const run: LabelRun = { ...runStyleOf(node.marks), text }
    const last = runs.at(-1)
    if (last && JSON.stringify({ ...last, text: '' }) === JSON.stringify({ ...run, text: '' })) last.text += text
    else runs.push(run)
  }
  return runs
}

/** A document as blocks: a list nested in another becomes more items of the outer kind's level, since labels' lists are flat. */
export function blocksOf(doc: DocNode): LabelBlock[] {
  const blocks: LabelBlock[] = []
  const walk = (nodes: DocNode[] | undefined, kind: LabelBlock['kind']) => {
    for (const node of nodes ?? []) {
      if (node.type === 'paragraph') blocks.push({ kind, runs: runsOf(node.content) })
      else if (node.type === 'bulletList') walk(node.content, 'Bullet')
      else if (node.type === 'orderedList') walk(node.content, 'Numbered')
      else if (node.type === 'listItem') walk(node.content, kind)
    }
  }
  walk(doc.content, 'Paragraph')
  return blocks
}
