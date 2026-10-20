import Markdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { noteRoute, notePath } from '../board/links'

interface MarkdownViewProps {
  text: string
  className?: string
}

/**
 * Rendered Markdown (GitHub flavour). Raw HTML in the text is shown as text, never rendered, and react-markdown drops
 * unsafe link protocols. A link to a post-it opens it in place; any other link opens in a new tab.
 */
export function MarkdownView({ text, className }: MarkdownViewProps) {
  return (
    <div className={className ? `markdown ${className}` : 'markdown'}>
      <Markdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ node: _node, href, ...props }) => {
            const route = href ? noteRoute(href) : null
            // Only the hash changes: the board opens the post-it, or App switches to its board.
            if (route) return <a {...props} href={notePath(route).slice(1)} />
            return <a {...props} href={href} target="_blank" rel="noreferrer noopener" />
          },
        }}
      >
        {text}
      </Markdown>
    </div>
  )
}

export default MarkdownView
