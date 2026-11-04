import { useCallback, useState } from 'react'
import type { InkColor, LabelSize } from '../api/types'
import type { LabelEditorHandle } from './labels'
import type { LabelLinkField, LabelToolsProps } from './LabelView'

interface LabelToolsOptions {
  ink: InkColor
  size: LabelSize
  background: boolean
  onDelete: () => void
}

/** What the tools of a label being edited work on and show, on a canvas or on the mobile layout's own screen. */
export function useLabelTools({ ink: startInk, size: startSize, background: startBackground, onDelete }: LabelToolsOptions) {
  const [handle, setHandle] = useState<LabelEditorHandle | null>(null)
  const [ink, setInk] = useState(startInk)
  const [size, setSize] = useState(startSize)
  const [background, setBackground] = useState(startBackground)
  /** The selection changed: what the toolbar shows follows it. */
  const [, setTick] = useState(0)
  const [link, setLink] = useState<{ value: string; error: boolean; existing: boolean } | null>(null)

  const openLink = (from: LabelEditorHandle | null) => {
    const href = from?.linkHref() ?? null
    setLink({ value: href ?? '', error: false, existing: !!href })
  }

  const onChange = useCallback(() => setTick((t) => t + 1), [])

  function applyLink() {
    if (!link) return
    const url = link.value.trim()
    if (!handle?.setLink(url)) {
      setLink({ ...link, error: true })
      return
    }
    setLink(null)
  }

  const here = handle ? handle.styleHere({ ink, size }) : { ink, size }
  const field: LabelLinkField | null = link && {
    ...link,
    onChange: (value) => setLink({ ...link, value, error: false }),
    onApply: applyLink,
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
    onLink: () => (link ? setLink(null) : openLink(handle)),
    blocks: { isActive: (kind) => !!handle?.blockActive(kind), onToggle: (kind) => handle?.toggleBlock(kind) },
    style: {
      ink: here.ink,
      size: here.size,
      // For the text selected, or what is typed next at the cursor; only a label with no text yet takes it as its own.
      onInk: (value) => (handle && !handle.isEmpty() ? handle.setInk(value) : setInk(value)),
      onSize: (value) => (handle && !handle.isEmpty() ? handle.setSize(value) : setSize(value)),
    },
    background: { on: background, onToggle: () => setBackground((on) => !on) },
    onDelete,
    link: field,
  }
  return { handle, ink, size, background, onReady: setHandle, onChange, tools }
}
