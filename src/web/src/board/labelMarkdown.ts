import type { LabelBlock, LabelRun } from '../api/types'
import { isLabelLink } from './labels'

// Markdown pasted into a label: the rules of LabelMarkdown on the server, which keep the two alike.

type Style = Omit<LabelRun, 'text'>

function listItem(line: string): { kind: 'Bullet' | 'Numbered'; text: string } | null {
  const text = line.trimStart()
  const bullet = /^[-*] +(.*)$/.exec(text)
  if (bullet) return { kind: 'Bullet', text: bullet[1] }
  const numbered = /^\d+\. +(.*)$/.exec(text)
  return numbered ? { kind: 'Numbered', text: numbered[1] } : null
}

/** Where the mark opened at `start` closes; undefined when it opens nothing there (see LabelMarkdown.Mark). */
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
  while (i < text.length) {
    const end2 = markEnd(text, i, '**')
    const strike = end2 === undefined ? markEnd(text, i, '~~') : undefined
    const end1 = end2 === undefined && strike === undefined ? markEnd(text, i, '*') : undefined
    const link = text[i] === '[' ? linkAt(text, i) : undefined
    if (text[i] === '\\' && /[\p{P}\p{S}]/u.test(text[i + 1] ?? '')) {
      plain += text[i + 1]
      i += 2
    } else if (end2 !== undefined) {
      flush()
      addInline(text.slice(i + 2, end2), { ...style, bold: true }, runs)
      i = end2 + 2
    } else if (strike !== undefined) {
      flush()
      addInline(text.slice(i + 2, strike), { ...style, strike: true }, runs)
      i = strike + 2
    } else if (end1 !== undefined) {
      flush()
      addInline(text.slice(i + 1, end1), { ...style, italic: true }, runs)
      i = end1 + 1
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

/** The blocks some Markdown holds. */
export function parseLabelMarkdown(markdown: string): LabelBlock[] {
  const blocks: LabelBlock[] = []
  let paragraph: string[] = []
  const endParagraph = () => {
    if (paragraph.length > 0) blocks.push({ kind: 'Paragraph', runs: inline(paragraph.join('\n')) })
    paragraph = []
  }
  for (const line of markdown.replace(/\r\n/g, '\n').split('\n')) {
    const trimmed = line.trimEnd()
    const item = trimmed ? listItem(trimmed) : null
    if (!trimmed) {
      endParagraph()
    } else if (item) {
      endParagraph()
      blocks.push({ kind: item.kind, runs: inline(item.text) })
    } else {
      paragraph.push(trimmed)
    }
  }
  endParagraph()
  return blocks
}
