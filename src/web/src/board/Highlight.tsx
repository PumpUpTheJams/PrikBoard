import { matchRanges } from './search'

/** Text with the parts the search found in <mark>; plain text when there is nothing to look for. */
export function Highlight({ text, terms }: { text: string; terms?: string[] }) {
  const ranges = terms ? matchRanges(text, terms) : []
  if (ranges.length === 0) return <>{text}</>
  const parts = []
  let at = 0
  for (const [start, end] of ranges) {
    if (start > at) parts.push(text.slice(at, start))
    parts.push(<mark key={start}>{text.slice(start, end)}</mark>)
    at = end
  }
  if (at < text.length) parts.push(text.slice(at))
  return <>{parts}</>
}
