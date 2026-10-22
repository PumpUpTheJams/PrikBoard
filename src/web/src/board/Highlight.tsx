import { fieldRanges, type SearchField, type SearchTerm } from './search'

/**
 * Text with the parts the search found in <mark>; plain text when there is nothing to look for. `field` is what kind of
 * text it is, so a word kept to another field (`tag:ui`) isn't marked here.
 */
export function Highlight({ text, terms, field }: { text: string; terms?: SearchTerm[]; field: SearchField['kind'] }) {
  const ranges = terms ? fieldRanges(text, terms, field) : []
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
