import { labelInks, labelSizes, type LabelBlock, type LabelRun, type LabelSize } from '../api/types'
import { inkColor } from './drawings'
import { isLabelLink, isListItem, labelPixels } from './labels'

// A label's blocks as the editor's document and back. Only what labels have survives the way back, so whatever got
// into the editor (pasted colors) can't reach the server.

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
  if (run.code) marks.push({ type: 'code' })
  if (run.link) marks.push({ type: 'link', attrs: { href: run.link } })
  if (run.ink || run.size) {
    marks.push({ type: 'textStyle', attrs: { color: run.ink ? inkColor[run.ink] : null, fontSize: run.size ? fontSizeOf(run.size) : null } })
  }
  return marks.length > 0 ? marks : undefined
}

/** The font size of a label size, as the editor's text style holds it, and back; likewise inks. */
export const fontSizeOf = (size: LabelSize) => `${labelPixels[size]}px`
export const sizeOf = (fontSize: unknown) => labelSizes.find((s) => fontSizeOf(s) === fontSize)
export const inkOf = (color: unknown) => labelInks.find((c) => inkColor[c] === color)

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

function paragraphOf(runs: LabelRun[]): DocNode {
  const content = inlineOf(runs)
  return { type: 'paragraph', ...(content.length > 0 ? { content } : {}) }
}

const listTypes = { Bullet: 'bulletList', Numbered: 'orderedList', Task: 'taskList' } as const
const listKinds: Record<string, keyof typeof listTypes> = { bulletList: 'Bullet', orderedList: 'Numbered', taskList: 'Task' }

const cellOf = (cell: LabelRun[], type: string): DocNode => ({ type, content: [paragraphOf(cell)] })

/** Blocks as a document. */
export function docOf(blocks: LabelBlock[]): DocNode {
  const content: DocNode[] = []
  // The lists the last list item is in, outermost first.
  let lists: { node: DocNode; level: number }[] = []
  for (const block of blocks) {
    const last = content.at(-1)
    if (isListItem(block)) {
      const type = listTypes[block.kind as keyof typeof listTypes]
      const level = block.level ?? 0
      lists = lists.filter((l) => l.level < level || (l.level === level && l.node.type === type))
      if (lists.at(-1)?.level !== level) {
        const node: DocNode = { type, content: [] }
        const around = lists.at(-1)?.node.content!.at(-1)
        if (around) around.content!.push(node)
        else content.push(node)
        lists.push({ node, level })
      }
      lists.at(-1)!.node.content!.push(
        block.kind === 'Task'
          ? { type: 'taskItem', attrs: { checked: block.done === true }, content: [paragraphOf(block.runs)] }
          : { type: 'listItem', content: [paragraphOf(block.runs)] },
      )
      continue
    }
    lists = []
    if (block.kind === 'Quote') {
      if (last?.type === 'blockquote') last.content!.push(paragraphOf(block.runs))
      else content.push({ type: 'blockquote', content: [paragraphOf(block.runs)] })
    } else if (block.kind === 'TableRow') {
      const header = last?.type !== 'table'
      const row: DocNode = { type: 'tableRow', content: (block.cells ?? []).map((cell) => cellOf(cell, header ? 'tableHeader' : 'tableCell')) }
      if (header) content.push({ type: 'table', content: [row] })
      else last.content!.push(row)
    } else if (block.kind === 'Heading') {
      content.push({ ...paragraphOf(block.runs), type: 'heading', attrs: { level: block.level ?? 1 } })
    } else if (block.kind === 'Code') {
      const text = block.runs.map((r) => r.text).join('')
      content.push({ type: 'codeBlock', ...(text ? { content: [{ type: 'text', text }] } : {}) })
    } else if (block.kind === 'Rule') {
      content.push({ type: 'horizontalRule' })
    } else {
      content.push(paragraphOf(block.runs))
    }
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
    else if (mark.type === 'code') style.code = true
    else if (mark.type === 'link' && typeof mark.attrs?.href === 'string' && isLabelLink(mark.attrs.href)) style.link = mark.attrs.href
    else if (mark.type === 'textStyle') {
      const ink = inkOf(mark.attrs?.color)
      const size = sizeOf(mark.attrs?.fontSize)
      if (ink) style.ink = ink
      if (size) style.size = size
    }
  }
  return style
}

function runsOfNodes(nodes: DocNode[] | undefined): LabelRun[] {
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

/** A document as blocks; nesting becomes the level of each list item. */
export function blocksOf(doc: DocNode): LabelBlock[] {
  const blocks: LabelBlock[] = []
  const withLevel = (kind: LabelBlock['kind'], level: number) => (level > 0 ? { kind, level } : { kind })
  const walk = (nodes: DocNode[] | undefined, kind: LabelBlock['kind'], level: number, done?: boolean) => {
    for (const node of nodes ?? []) {
      if (node.type === 'paragraph') blocks.push({ ...withLevel(kind, level), ...(done ? { done } : {}), runs: runsOfNodes(node.content) })
      else if (node.type === 'heading') blocks.push({ kind: 'Heading', level: Number(node.attrs?.level ?? 1), runs: runsOfNodes(node.content) })
      else if (node.type === 'codeBlock') blocks.push({ kind: 'Code', runs: runsOfNodes(node.content).map(({ text }) => ({ text })) })
      else if (node.type === 'horizontalRule') blocks.push({ kind: 'Rule', runs: [] })
      else if (node.type === 'blockquote') walk(node.content, 'Quote', 0)
      else if (node.type === 'table') {
        for (const row of node.content ?? []) {
          // A cell's paragraphs become lines of one cell.
          const cells = (row.content ?? []).map((cell) => (cell.content ?? []).flatMap((p, i) => [...(i > 0 ? [{ text: '\n' }] : []), ...runsOfNodes(p.content)]))
          blocks.push({ kind: 'TableRow', runs: [], cells })
        }
      } else if (listKinds[node.type]) {
        const nested = kind in listTypes
        for (const item of node.content ?? []) walk(item.content, listKinds[node.type], nested ? level + 1 : 0, item.attrs?.checked === true)
      }
    }
  }
  walk(doc.content, 'Paragraph', 0)
  return blocks
}
