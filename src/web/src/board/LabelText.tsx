import { createElement, memo, type ReactNode } from 'react'
import type { LabelBlock, LabelRun } from '../api/types'
import { groupedBlocks, runStyle } from './labels'

function Run({ run }: { run: LabelRun }) {
  const style = runStyle(run)
  const text = run.code ? <code>{run.text}</code> : run.text
  if (!run.link) return <span style={style}>{text}</span>
  // A card's link opens in place, in this tab; others in a new one.
  const web = !run.link.startsWith('/')
  return (
    <a href={run.link} style={style} draggable={false} {...(web ? { target: '_blank', rel: 'noopener noreferrer' } : {})}>
      {text}
    </a>
  )
}

const runs = (list: LabelRun[]): ReactNode => list.map((run, i) => <Run key={i} run={run} />)
const line = (block: LabelBlock): ReactNode => (block.runs.length === 0 ? <br /> : runs(block.runs))

/** List items as lists: items of one kind on one level form a list, and deeper ones go in the item before them. */
function ListOf({ blocks }: { blocks: LabelBlock[] }) {
  const level = Math.min(...blocks.map((b) => b.level ?? 0))
  const items: { block: LabelBlock; inner: LabelBlock[] }[] = []
  for (const block of blocks) {
    if ((block.level ?? 0) > level && items.length > 0) items.at(-1)!.inner.push(block)
    else items.push({ block, inner: [] })
  }
  const lists: (typeof items)[] = []
  for (const item of items) {
    const last = lists.at(-1)
    if (last && (last[0].block.kind === 'Numbered') === (item.block.kind === 'Numbered')) last.push(item)
    else lists.push([item])
  }
  return lists.map((list, i) => {
    const shown = list.map(({ block, inner }, j) => (
      <li key={j} className={block.kind === 'Task' ? 'task' : undefined}>
        {block.kind === 'Task' ? (
          <>
            <input type="checkbox" checked={block.done === true} disabled readOnly />
            <div>
              {line(block)}
              {inner.length > 0 && <ListOf blocks={inner} />}
            </div>
          </>
        ) : (
          <>
            {line(block)}
            {inner.length > 0 && <ListOf blocks={inner} />}
          </>
        )}
      </li>
    ))
    return list[0].block.kind === 'Numbered' ? <ol key={i}>{shown}</ol> : <ul key={i}>{shown}</ul>
  })
}

function TableOf({ blocks }: { blocks: LabelBlock[] }) {
  const [head, ...rows] = blocks
  return (
    <table>
      <thead>
        <tr>{(head.cells ?? []).map((cell, i) => <th key={i}>{runs(cell)}</th>)}</tr>
      </thead>
      {rows.length > 0 && (
        <tbody>
          {rows.map((row, r) => (
            <tr key={r}>{(row.cells ?? []).map((cell, i) => <td key={i}>{runs(cell)}</td>)}</tr>
          ))}
        </tbody>
      )}
    </table>
  )
}

function One({ block }: { block: LabelBlock }) {
  if (block.kind === 'Heading') return createElement(`h${block.level ?? 1}`, null, line(block))
  if (block.kind === 'Rule') return <hr />
  if (block.kind === 'Code') {
    return (
      <pre>
        <code>{block.runs.map((r) => r.text).join('')}</code>
      </pre>
    )
  }
  return <p>{line(block)}</p>
}

/** A label's text, drawn from its blocks. It needs no editor, so a board full of labels costs little. */
export const LabelText = memo(function LabelText({ content }: { content: LabelBlock[] }) {
  return groupedBlocks(content).map((group, i) => {
    if (group.kind === 'List') return <ListOf key={i} blocks={group.blocks} />
    if (group.kind === 'Table') return <TableOf key={i} blocks={group.blocks} />
    if (group.kind === 'Quote') {
      return (
        <blockquote key={i}>
          {group.blocks.map((block, j) => (
            <p key={j}>{line(block)}</p>
          ))}
        </blockquote>
      )
    }
    return <One key={i} block={group.blocks[0]} />
  })
})
