import { useRef, useState } from 'react'
import type { CardComponent, CardComponentKind, CardDetail } from '../api/types'
import { ChecklistComponent, ComponentFrame, LinksComponent, TextComponent } from '../board/CardComponents'
import { componentKinds } from '../board/componentKinds'
import { TrashIcon } from '../board/icons'

/** A component's content, without where it sits on the card's canvas. */
export type ComponentDraft = Omit<CardComponent, 'id' | 'x' | 'y' | 'width' | 'height'>

interface ComponentEditorProps {
  kind: CardComponentKind
  card: CardDetail
  cards: CardDetail[]
  /** The component being edited; without one, a new one is written. */
  component?: CardComponent
  onCancel: () => void
  /** Saves it (adds a new one, or changes the existing one); the screen closes when that worked. */
  onSave: (draft: ComponentDraft) => Promise<void>
  /** Removes the component being edited. */
  onRemove?: () => void
}

/**
 * A component written on a screen of its own, new or opened with its edit button: the same frame and editor as on the
 * desktop card, kept as a draft until Add or Save (Cancel drops it, and nothing reaches the board).
 */
export function ComponentEditor({ kind, card, cards, component, onCancel, onSave, onRemove }: ComponentEditorProps) {
  const { label } = componentKinds[kind]
  const [draft, setDraft] = useState<ComponentDraft>(() => ({
    kind,
    title: component?.title ?? null,
    text: component?.text ?? null,
    items: component?.items ?? (kind === 'Checklist' ? [] : null),
    links: component?.links ?? (kind === 'Links' ? [] : null),
  }))
  /** The latest draft: a text saves on blur, which happens as Add is pressed, before the click is handled. */
  const latest = useRef(draft)
  const [busy, setBusy] = useState(false)

  function change(changes: Partial<ComponentDraft>) {
    latest.current = { ...latest.current, ...changes }
    setDraft(latest.current)
    return Promise.resolve(true)
  }

  function save() {
    ;(document.activeElement as HTMLElement | null)?.blur()
    // After the blur's save has updated the draft.
    setTimeout(() => {
      setBusy(true)
      void onSave(latest.current).finally(() => setBusy(false))
    })
  }

  const shown: CardComponent = { ...draft, id: component?.id ?? 'new', x: 0, y: 0, width: 0, height: 0 }

  return (
    <div className="m-screen m-editor">
      <header className="m-bar">
        <button type="button" className="m-bar-text" onClick={onCancel}>
          Cancel
        </button>
        <div className="m-bar-title centered">
          <span className="m-bar-name">
            {component ? 'Edit' : 'New'} {label.toLowerCase()}
          </span>
          <span className="m-bar-context mono">{card.readableId}</span>
        </div>
        <button type="button" className="m-bar-primary" disabled={busy} onClick={save}>
          {component ? 'Save' : 'Add'}
        </button>
      </header>

      <div className="m-scroll m-stack">
        <ComponentFrame
          component={shown}
          selected={false}
          lifted={false}
          // Removing is a button of its own below (the frame's small one is hidden here).
          onSelect={() => {}}
          onTitle={(title) => void change({ title: title || null })}
          onRemove={onRemove ?? onCancel}
        >
          {kind === 'Text' && <TextComponent text={draft.text ?? ''} autoFocus onChange={(text) => void change({ text })} />}
          {kind === 'Checklist' && <ChecklistComponent items={draft.items ?? []} autoFocus onChange={(items) => change({ items })} />}
          {kind === 'Links' && <LinksComponent links={draft.links ?? []} card={card} cards={cards} autoFocus onChange={(links) => change({ links })} />}
        </ComponentFrame>
        <p className="m-note">
          {kind === 'Checklist'
            ? 'Enter adds an item and keeps the field open for the next.'
            : kind === 'Links'
              ? 'Paste an http(s) address, or type to find a card on this board.'
              : 'Markdown works: **bold**, lists, links.'}
        </p>
        {component && onRemove && (
          <button type="button" className="m-action danger" onClick={onRemove}>
            <TrashIcon />
            Remove {label.toLowerCase()}
          </button>
        )}
      </div>
    </div>
  )
}
