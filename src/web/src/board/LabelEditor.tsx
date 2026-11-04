import { useEffect, useMemo } from 'react'
import { TaskItem, TaskList } from '@tiptap/extension-list'
import { Table, TableCell, TableHeader, TableRow } from '@tiptap/extension-table'
import { TextStyleKit } from '@tiptap/extension-text-style'
import { EditorContent, useEditor, type Editor, type JSONContent } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import { labelInks, labelSizes, type InkColor, type LabelBlock, type LabelSize } from '../api/types'
import { inkColor } from './drawings'
import { blocksOf, docOf, type DocNode } from './labelDoc'
import { parseLabelMarkdown } from './labelMarkdown'
import { isLabelLink, labelPixels, type LabelBlockTool, type LabelEditorHandle, type LabelFormat } from './labels'

interface LabelEditorProps {
  content: LabelBlock[]
  /** The editor has loaded: what the toolbar works through. */
  onReady: (handle: LabelEditorHandle) => void
  /** The selection or text changed, so the toolbar shows what is on there. */
  onChange: () => void
  /** Esc: done editing. */
  onDone: () => void
}

const blockNodes: Record<LabelBlockTool, string> = { Heading: 'heading', Task: 'taskList', Quote: 'blockquote', Code: 'codeBlock', Table: 'table' }

/** Lazy loaded: Tiptap is big, and only those who edit a label need it. Viewing one takes `LabelText`. */
export default function LabelEditor({ content, onReady, onChange, onDone }: LabelEditorProps) {
  const editor: Editor = useEditor({
    extensions: [
      StarterKit.configure({
        dropcursor: false,
        gapcursor: false,
        trailingNode: false,
        link: { openOnClick: false, autolink: false, linkOnPaste: false, enableClickSelection: false, isAllowedUri: (url) => isLabelLink(url), HTMLAttributes: { target: null, rel: null } },
      }),
      TextStyleKit.configure({ backgroundColor: false, fontFamily: false, lineHeight: false }),
      TaskList,
      TaskItem.configure({ nested: true }),
      Table.configure({ resizable: false, cellMinWidth: 0 }),
      TableRow,
      TableHeader,
      TableCell,
    ],
    content: docOf(content) as JSONContent,
    // Not by the autofocus option, which scrolls to the end: the board stays where it is until something is typed. And not from
    // an effect, which can get an editor already destroyed: Tiptap drops the one of a render React gave up on.
    onCreate: ({ editor: created }) => void created.commands.focus('end', { scrollIntoView: false }),
    onSelectionUpdate: onChange,
    onTransaction: onChange,
    editorProps: {
      handleKeyDown(_view, event) {
        if (event.key === 'Escape') {
          // Done here: the board and the card don't get it, or it would also let go of the card.
          event.preventDefault()
          event.stopPropagation()
          onDone()
          return true
        }
        // Tab nests a list item and moves through a table; anywhere else it stays out of the way rather than leaving the label.
        if (event.key === 'Tab') {
          event.preventDefault()
          const chain = editor.chain()
          if (editor.isActive('table')) void (event.shiftKey ? chain.goToPreviousCell() : chain.goToNextCell()).run()
          else {
            const item = editor.isActive('taskItem') ? 'taskItem' : 'listItem'
            if (editor.isActive(item)) void (event.shiftKey ? chain.liftListItem(item) : chain.sinkListItem(item)).run()
          }
          return true
        }
        return false
      },
      handlePaste(_view, event): boolean {
        const data = event.clipboardData
        // What another editor copied has its own formatting; plain text is read as Markdown.
        if (!data || data.getData('text/html')) return false
        const text = data.getData('text/plain')
        if (!text.includes('\n') && !/[*~`[\]]/.test(text) && !/^\s*(?:[-*>#]|\d+\.) /.test(text)) return false
        const parsed = parseLabelMarkdown(text)
        if (parsed.length === 0) return false
        const single = parsed.length === 1 && parsed[0].kind === 'Paragraph'
        const doc = docOf(parsed) as JSONContent
        return editor.commands.insertContent(single ? (doc.content![0].content ?? []) : doc.content!)
      },
    },
  })

  const handle = useMemo((): LabelEditorHandle => ({
    blocks: () => blocksOf(editor.getJSON() as DocNode),
    focus: () => editor.commands.focus(),
    isEmpty: () => editor.isEmpty,
    styleHere(base) {
      const styleOf = (attrs: Record<string, unknown>) => ({
        ink: labelInks.find((c) => inkColor[c] === attrs.color) ?? base.ink,
        size: labelSizes.find((s) => `${labelPixels[s]}px` === attrs.fontSize) ?? base.size,
      })
      const { empty, from, to } = editor.state.selection
      if (empty) return styleOf(editor.getAttributes('textStyle'))
      const inks = new Set<InkColor>()
      const sizes = new Set<LabelSize>()
      editor.state.doc.nodesBetween(from, to, (node) => {
        if (!node.isText) return
        const style = styleOf(node.marks.find((m) => m.type.name === 'textStyle')?.attrs ?? {})
        inks.add(style.ink)
        sizes.add(style.size)
      })
      return { ink: inks.size === 1 ? [...inks][0] : null, size: sizes.size === 1 ? [...sizes][0] : null }
    },
    isActive: (format: LabelFormat) => editor.isActive(format),
    toggle(format) {
      const chain = editor.chain().focus()
      if (format === 'bold') chain.toggleBold().run()
      else if (format === 'italic') chain.toggleItalic().run()
      else if (format === 'underline') chain.toggleUnderline().run()
      else if (format === 'code') chain.toggleCode().run()
      else chain.toggleStrike().run()
    },
    listActive: (kind) => editor.isActive(kind === 'Bullet' ? 'bulletList' : 'orderedList'),
    toggleList(kind) {
      const chain = editor.chain().focus()
      if (kind === 'Bullet') chain.toggleBulletList().run()
      else chain.toggleOrderedList().run()
    },
    blockActive: (kind: LabelBlockTool) => editor.isActive(blockNodes[kind]),
    toggleBlock(kind) {
      const chain = editor.chain().focus()
      if (kind === 'Heading') chain.toggleHeading({ level: 2 }).run()
      else if (kind === 'Task') chain.toggleTaskList().run()
      else if (kind === 'Quote') chain.toggleBlockquote().run()
      else if (kind === 'Code') chain.toggleCodeBlock().run()
      else if (editor.isActive('table')) chain.deleteTable().run()
      else chain.insertTable({ rows: 3, cols: 2, withHeaderRow: true }).run()
    },
    setInk: (ink) => void editor.chain().focus().setColor(inkColor[ink]).run(),
    setSize: (size) => void editor.chain().focus().setFontSize(`${labelPixels[size]}px`).run(),
    linkHref: () => (editor.getAttributes('link').href as string | undefined) ?? null,
    setLink(url) {
      if (!isLabelLink(url)) return false
      // On a link, the whole link changes; with nothing selected, the address is the text.
      const chain = editor.chain().focus().extendMarkRange('link')
      if (editor.state.selection.empty && !editor.isActive('link')) {
        return chain.insertContent({ type: 'text', text: url, marks: [{ type: 'link', attrs: { href: url } }] }).run()
      }
      return chain.setLink({ href: url }).run()
    },
    unsetLink: () => void editor.chain().focus().extendMarkRange('link').unsetLink().run(),
  }), [editor])

  useEffect(() => onReady(handle), [onReady, handle])

  return <EditorContent editor={editor} />
}
