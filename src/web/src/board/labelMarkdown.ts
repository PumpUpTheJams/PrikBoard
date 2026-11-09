import type { LabelBlock, LabelRun } from '../api/types'
import { isLabelLink } from './labels'

// Markdown pasted into a label: the rules of LabelMarkdown on the server, which keep the two alike.

type Style = Omit<LabelRun, 'text'>

function listItem(line: string): { kind: 'Bullet' | 'Numbered' | 'Task'; text: string; done?: boolean } | null {
  const text = line.trimStart()
  const bullet = /^[-*] +(.*)$/.exec(text)
  if (bullet) {
    const task = /^\[( |x|X)\] +(.*)$/.exec(bullet[1])
    return task ? { kind: 'Task', text: task[2], done: task[1] !== ' ' } : { kind: 'Bullet', text: bullet[1] }
  }
  const numbered = /^\d+\. +(.*)$/.exec(text)
  return numbered ? { kind: 'Numbered', text: numbered[1] } : null
}

/** An http(s) address starting at `start` (see LabelMarkdown.BareLink). */
function bareLinkAt(text: string, start: number): string | undefined {
  if (start > 0 && /[\p{L}\p{N}]/u.test(text[start - 1])) return undefined
  const found = /^https?:\/\/[^\s<>`|]+/.exec(text.slice(start))?.[0]
  if (!found) return undefined
  let url = found.replace(/[.,;:!?*~_"']+$/, '')
  while (url.endsWith(')') && url.split(')').length > url.split('(').length) url = url.slice(0, -1)
  return isLabelLink(url) ? url : undefined
}

/** The code span opened at `start`: closed by as many backticks as open it (see LabelMarkdown.CodeSpan). */
function codeSpanAt(text: string, start: number): { text: string; end: number } | undefined {
  let ticks = 0
  while (text[start + ticks] === '`') ticks++
  for (let j = start + ticks; j < text.length; j++) {
    if (text[j] !== '`') continue
    let closing = 0
    while (text[j + closing] === '`') closing++
    if (closing === ticks) {
      let body = text.slice(start + ticks, j)
      if (body.length > 2 && body.startsWith(' ') && body.endsWith(' ')) body = body.slice(1, -1)
      return body ? { text: body, end: j + closing } : undefined
    }
    j += closing - 1
  }
  return undefined
}

/** Where the mark opened at `start` closes; undefined when it opens nothing there (see LabelMarkdown.Mark). */
/** The marks text can open with, longest first, and the style each gives. */
const MARKS: { text: string; style: Partial<LabelRun> }[] = [
  { text: '**', style: { bold: true } },
  { text: '~~', style: { strike: true } },
  { text: '*', style: { italic: true } },
]

/** The mark that opens at `i` and where it closes, if any. */
function markAt(text: string, i: number) {
  for (const mark of MARKS) {
    const end = markEnd(text, i, mark.text)
    if (end !== undefined) return { ...mark, end }
  }
  return undefined
}

function markEnd(text: string, start: number, mark: string): number | undefined {
  if (!text.startsWith(mark, start)) return undefined
  const first = start + mark.length
  if (first >= text.length || /\s/.test(text[first])) return undefined
  for (let j = first; j < text.length; j++) {
    if (text[j] === '\\') {
      j++
      continue
    }
    if (mark === '*' && text[j] === '*' && text[j + 1] === '*') {
      j++
      continue
    }
    if (!text.startsWith(mark, j) || j === first || /\s/.test(text[j - 1])) continue
    while (mark === '**' && text[j + 2] === '*') j++
    return j
  }
  return undefined
}

function linkAt(text: string, start: number): { text: string; url: string; end: number } | undefined {
  let close = start + 1
  while (close < text.length && text[close] !== ']') close += text[close] === '\\' ? 2 : 1
  if (text[close + 1] !== '(') return undefined
  const end = text.indexOf(')', close + 2)
  if (end < 0) return undefined
  const url = text.slice(close + 2, end).trim()
  return close > start + 1 && isLabelLink(url) ? { text: text.slice(start + 1, close), url, end: end + 1 } : undefined
}

function addInline(text: string, style: Style, runs: LabelRun[]) {
  let plain = ''
  const flush = () => {
    if (!plain) return
    runs.push({ ...style, text: plain })
    plain = ''
  }
  let i = 0
  let mark: ReturnType<typeof markAt>
  while (i < text.length) {
    const link = text[i] === '[' ? linkAt(text, i) : undefined
    const code = text[i] === '`' ? codeSpanAt(text, i) : undefined
    const bare = !style.link && text[i] === 'h' ? bareLinkAt(text, i) : undefined
    if (text[i] === '\\' && /[\p{P}\p{S}]/u.test(text[i + 1] ?? '')) {
      plain += text[i + 1]
      i += 2
    } else if (bare) {
      flush()
      runs.push({ ...style, text: bare, link: bare })
      i += bare.length
    } else if (code) {
      flush()
      runs.push({ ...style, text: code.text, code: true })
      i = code.end
    } else if ((mark = markAt(text, i))) {
      flush()
      addInline(text.slice(i + mark.text.length, mark.end), { ...style, ...mark.style }, runs)
      i = mark.end + mark.text.length
    } else if (link) {
      flush()
      addInline(link.text, { ...style, link: link.url }, runs)
      i = link.end
    } else {
      plain += text[i++]
    }
  }
  flush()
}

const inline = (text: string) => {
  const runs: LabelRun[] = []
  addInline(text, {}, runs)
  return runs
}

function tableRow(line: string): LabelBlock {
  let text = line.trim()
  if (text.startsWith('|')) text = text.slice(1)
  if (text.endsWith('|') && !text.endsWith('\\|')) text = text.slice(0, -1)
  const cells: LabelRun[][] = []
  let start = 0
  for (let i = 0; i <= text.length; i++) {
    if (text[i] === '\\') {
      i++
    } else if (i >= text.length || text[i] === '|') {
      cells.push(inline(text.slice(start, i).trim()))
      start = i + 1
    }
  }
  return { kind: 'TableRow', runs: [], cells }
}

/** The fence a line opens code with, or null. */
function fenceOf(line: string): string | null {
  const text = line.trimStart()
  const ticks = /^`{3,}/.exec(text)
  if (ticks) return text.indexOf('`', ticks[0].length) < 0 ? ticks[0] : null
  return /^~{3,}/.exec(text)?.[0] ?? null
}

const MAX_LIST_LEVEL = 5

/** The blocks some Markdown holds. */
export function parseLabelMarkdown(markdown: string): LabelBlock[] {
  const blocks: LabelBlock[] = []
  let paragraph: string[] = []
  let quote: string[] = []
  // The indents of the lists around the current item, outermost first.
  let indents: number[] = []
  let afterItem = false
  const endParagraph = () => {
    if (paragraph.length > 0) blocks.push({ kind: 'Paragraph', runs: inline(paragraph.join('\n')) })
    paragraph = []
  }
  const startBlock = () => {
    endParagraph()
    indents = []
  }
  const endQuote = () => {
    if (quote.length > 0) blocks.push({ kind: 'Quote', runs: inline(quote.join('\n')) })
    quote = []
  }
  const lines = markdown.replace(/\r\n/g, '\n').split('\n')
  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trimEnd()
    const wasAfterItem = afterItem
    afterItem = false
    if (!trimmed) {
      endParagraph()
      endQuote()
      continue
    }
    const fence = fenceOf(trimmed)
    if (fence) {
      endParagraph()
      endQuote()
      indents = []
      const code: string[] = []
      for (i++; i < lines.length && !lines[i].trim().startsWith(fence); i++) code.push(lines[i])
      blocks.push({ kind: 'Code', runs: code.length > 0 ? [{ text: code.join('\n') }] : [] })
      continue
    }
    if (trimmed.trimStart().startsWith('>')) {
      startBlock()
      const rest = trimmed.trimStart().slice(1).replace(/^ /, '')
      if (rest) quote.push(rest)
      else endQuote()
      continue
    }
    endQuote()
    const heading = /^ {0,3}(#{1,6}) +(.*?)(?: +#+)?$/.exec(trimmed)
    const item = listItem(trimmed)
    if (heading) {
      startBlock()
      blocks.push({ kind: 'Heading', level: heading[1].length, runs: inline(heading[2]) })
    } else if (/^ {0,3}(?:-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
      startBlock()
      blocks.push({ kind: 'Rule', runs: [] })
    } else if (trimmed.includes('|') && /^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$/.test(lines[i + 1]?.trimEnd() ?? '')) {
      startBlock()
      blocks.push(tableRow(trimmed))
      for (i += 2; i < lines.length && lines[i].includes('|') && lines[i].trim(); i++) blocks.push(tableRow(lines[i].trimEnd()))
      i--
    } else if (item) {
      endParagraph()
      const indent = trimmed.length - trimmed.trimStart().length
      indents = indents.filter((at) => at <= indent)
      if (indents.at(-1) !== indent) indents.push(indent)
      const level = Math.min(indents.length - 1, MAX_LIST_LEVEL)
      blocks.push({ kind: item.kind, runs: inline(item.text), ...(level > 0 ? { level } : {}), ...(item.done ? { done: true } : {}) })
      afterItem = true
    } else if (wasAfterItem) {
      // A line right after a list item goes on with it.
      blocks.at(-1)!.runs.push(...inline(`\n${trimmed.trimStart()}`))
      afterItem = true
    } else {
      indents = []
      paragraph.push(trimmed)
    }
  }
  endParagraph()
  endQuote()
  return blocks
}
