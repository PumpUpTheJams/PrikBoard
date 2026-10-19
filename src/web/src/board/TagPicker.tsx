import { useState } from 'react'
import type { BoardTag } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { PickerWindow, type PickerRow } from '../components/PickerWindow'
import { CheckIcon, TrashIcon } from './icons'
import { MAX_TAG_LENGTH, MAX_TAGS, normalizeTag, useDraftList } from './tags'

interface TagPickerProps {
  anchor: DOMRect
  /** The note's tags. */
  tags: string[]
  /** The board's tags, by name. */
  boardTags: BoardTag[]
  /** Saves the note's whole tag list; new names are added to the board by the server. */
  onChange: (tags: string[]) => Promise<boolean>
  /** Renames a board tag on every note. */
  onRename: (tag: BoardTag, name: string) => Promise<boolean>
  /** Deletes a board tag from every note; called after the person confirmed. */
  onDelete: (tag: BoardTag) => Promise<boolean>
  onClose: () => void
}

/** Picks a post-it's tags from the board's tags; typing a new name creates one. Each tag can be renamed or deleted. */
export function TagPicker({ anchor, tags, boardTags, onChange, onRename, onDelete, onClose }: TagPickerProps) {
  const [draft, change] = useDraftList(tags, onChange)
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<{ tag: BoardTag; name: string } | null>(null)
  const [deleting, setDeleting] = useState<BoardTag | null>(null)

  const wanted = normalizeTag(query)
  const full = draft.length >= MAX_TAGS
  const toggle = (name: string) => change(draft.includes(name) ? draft.filter((t) => t !== name) : [...draft, name])
  const matches = boardTags.filter((t) => t.name.includes(wanted))

  async function rename() {
    if (!editing) return
    const name = normalizeTag(editing.name)
    if (name.length === 0 || name === editing.tag.name) {
      setEditing(null)
      return
    }
    if (await onRename(editing.tag, name)) setEditing(null)
  }

  const rows: PickerRow[] = []
  if (wanted && !boardTags.some((t) => t.name === wanted) && !draft.includes(wanted)) {
    rows.push({
      key: 'create',
      label: `Create “${wanted.slice(0, MAX_TAG_LENGTH)}”`,
      detail: wanted.length > MAX_TAG_LENGTH ? `At most ${MAX_TAG_LENGTH} characters` : 'New tag on this board',
      disabled: full || wanted.length > MAX_TAG_LENGTH,
      onSelect: () => {
        toggle(wanted)
        setQuery('')
      },
    })
  }
  for (const tag of matches) {
    const checked = draft.includes(tag.name)
    rows.push({
      key: tag.id,
      label: tag.name,
      meta: tag.noteCount,
      checked,
      disabled: !checked && full,
      onSelect: () => toggle(tag.name),
      onEdit: () => setEditing({ tag, name: tag.name }),
      editor:
        editing?.tag.id === tag.id ? (
          <form
            className="picker-editor"
            onSubmit={(e) => {
              e.preventDefault()
              void rename()
            }}
          >
            <input
              autoFocus
              className="mono"
              aria-label={`New name for ${tag.name}`}
              value={editing.name}
              maxLength={MAX_TAG_LENGTH}
              onChange={(e) => setEditing({ tag, name: e.target.value })}
              onKeyDown={(e) => {
                if (e.key === 'Escape') {
                  e.stopPropagation()
                  setEditing(null)
                }
              }}
            />
            <button type="submit" className="picker-edit" aria-label="Save name" title="Save name">
              <CheckIcon size={13} />
            </button>
            <button
              type="button"
              className="picker-edit danger"
              aria-label={`Delete tag ${tag.name}`}
              title="Delete from the board"
              onClick={() => setDeleting(tag)}
            >
              <TrashIcon size={14} />
            </button>
          </form>
        ) : undefined,
    })
  }

  return (
    <>
      <PickerWindow
        anchor={anchor}
        label="Tags"
        placeholder="Find or add a tag…"
        query={query}
        onQueryChange={setQuery}
        chips={draft.map((name) => ({ key: name, label: name, onRemove: () => toggle(name) }))}
        rows={rows}
        empty={boardTags.length === 0 ? 'No tags on this board yet: type one.' : 'No tag matches.'}
        footer={full ? `A post-it has at most ${MAX_TAGS} tags.` : undefined}
        holdOpen={deleting !== null}
        onClose={onClose}
      />
      {deleting && (
        <ConfirmDialog
          title={`Delete the tag “${deleting.name}”?`}
          message={
            deleting.noteCount === 0
              ? 'No post-it uses it. It is removed from the board.'
              : `It is removed from the board and from ${deleting.noteCount === 1 ? '1 post-it' : `${deleting.noteCount} post-its`}.`
          }
          confirmLabel="Delete tag"
          onConfirm={() => {
            const tag = deleting
            setDeleting(null)
            setEditing(null)
            void onDelete(tag)
          }}
          onCancel={() => setDeleting(null)}
        />
      )}
    </>
  )
}
