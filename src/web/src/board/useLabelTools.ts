import { useCallback, useState } from 'react'
import type { InkColor, LabelSize } from '../api/types'
import type { LabelEditorHandle } from './labels'
import type { LabelLinkField, LabelToolsProps } from './LabelView'

interface LabelToolsOptions {
  ink: InkColor
  size: LabelSize
  background: boolean
  title: string
  onDelete: () => void
  /** Enter in the caption's field: done with the label. Without it, the text gets the focus back. */
  onDone?: () => void
}

/** What the tools of a label being edited work on and show, on a canvas or on the mobile layout's own screen. */
export function useLabelTools({ ink: startInk, size: startSize, background: startBackground, title: startTitle, onDelete, onDone }: LabelToolsOptions) {
  const [handle, setHandle] = useState<LabelEditorHandle | null>(null)
  const [ink, setInk] = useState(startInk)
  const [size, setSize] = useState(startSize)
  const [background, setBackground] = useState(startBackground)
  const [title, setTitle] = useState(startTitle)
  /** The selection changed: what the toolbar shows follows it. */
  const [, setTick] = useState(0)
  const [link, setLink] = useState<{ value: string; error: boolean; existing: boolean } | null>(null)

  const onChange = useCallback(() => setTick((t) => t + 1), [])

  const here = handle ? handle.styleHere({ ink, size }) : { ink, size }
  const field: LabelLinkField | null = link && {
    ...link,
    onChange: (value) => setLink({ ...link, value, error: false }),
    onApply: () => setLink(handle?.setLink(link.value.trim()) ? null : { ...link, error: true }),
    onRemove: () => {
      handle?.unsetLink()
      setLink(null)
    },
    onCancel: () => {
      setLink(null)
      handle?.focus()
    },
  }

  const tools: LabelToolsProps = {
    isActive: (format) => !!handle?.isActive(format),
    onToggle: (format) => handle?.toggle(format),
    listActive: (kind) => !!handle?.listActive(kind),
    onToggleList: (kind) => handle?.toggleList(kind),
    linkActive: !!handle?.linkHref(),
    onLink: () => {
      const href = handle?.linkHref()
      setLink(link ? null : { value: href ?? '', error: false, existing: !!href })
    },
    blocks: { isActive: (kind) => !!handle?.blockActive(kind), onToggle: (kind) => handle?.toggleBlock(kind) },
    style: {
      ink: here.ink,
      size: here.size,
      // For the text selected, or what is typed next at the cursor; only a label with no text yet takes it as its own.
      onInk: (value) => (handle && !handle.isEmpty() ? handle.setInk(value) : setInk(value)),
      onSize: (value) => (handle && !handle.isEmpty() ? handle.setSize(value) : setSize(value)),
    },
    background: { on: background, onToggle: () => setBackground((on) => !on) },
    caption: { value: title, onChange: setTitle, onDone: () => (onDone ? onDone() : handle?.focus()) },
    onDelete,
    link: field,
  }
  return { handle, ink, size, background, title, onReady: setHandle, onChange, tools }
}
