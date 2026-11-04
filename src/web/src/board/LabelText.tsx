import { memo, type ReactNode } from 'react'
import type { LabelBlock, LabelRun } from '../api/types'
import { groupedBlocks, runStyle } from './labels'

function Run({ run }: { run: LabelRun }) {
  const style = runStyle(run)
  if (!run.link) return <span style={style}>{run.text}</span>
  // A card's link opens in place, in this tab; others in a new one.
  const web = !run.link.startsWith('/')
  return (
    <a href={run.link} style={style} draggable={false} {...(web ? { target: '_blank', rel: 'noopener noreferrer' } : {})}>
      {run.text}
    </a>
  )
}

const line = (block: LabelBlock): ReactNode => (block.runs.length === 0 ? <br /> : block.runs.map((run, i) => <Run key={i} run={run} />))

/** A label's text, drawn from its blocks. It needs no editor, so a board full of labels costs little. */
export const LabelText = memo(function LabelText({ content }: { content: LabelBlock[] }) {
  return groupedBlocks(content).map((group, i) => {
    if (group.kind === 'Paragraph') return <p key={i}>{line(group.blocks[0])}</p>
    const items = group.blocks.map((block, j) => <li key={j}>{line(block)}</li>)
    return group.kind === 'Bullet' ? <ul key={i}>{items}</ul> : <ol key={i}>{items}</ol>
  })
})
