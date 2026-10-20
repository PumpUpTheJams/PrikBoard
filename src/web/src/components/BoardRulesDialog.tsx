import { useState } from 'react'
import { rulesMaxLength, type BoardDetail } from '../api/types'
import { ConfirmDialog } from './ConfirmDialog'
import { Dialog } from './Dialog'
import { MarkdownView } from './MarkdownView'

/** Where an owner starts when the board has no rules yet. */
const starterRules = `## About this board
What the project is and who it is for.

## How to handle tickets
- Pick up notes from **To do**, call \`set_working\` and move them to **Doing**.
- When a note is done, move it to **Done** and stop working on it.

## Don't
- Delete notes; leave a comment in the body instead.
`

interface BoardRulesDialogProps {
  board: BoardDetail
  /** Saves new rules; an empty string clears them. Resolves to whether it worked. */
  onSave: (rules: string) => Promise<boolean>
  onClose: () => void
}

/**
 * The board's agent rules: Markdown that owners write about what the board is for and how agents should handle its
 * notes. Agents read them over MCP and follow them in every action. Everyone on the board can read them; owners edit.
 */
export function BoardRulesDialog({ board, onSave, onClose }: BoardRulesDialogProps) {
  const owner = board.yourRole === 'Owner'
  // The draft is local, so live reloads of the board don't overwrite what the owner is typing.
  const [draft, setDraft] = useState<string | null>(null)
  const [tab, setTab] = useState<'write' | 'preview'>('write')
  const [saving, setSaving] = useState(false)
  const [confirmDiscard, setConfirmDiscard] = useState<'close' | 'cancel' | null>(null)

  const editing = draft !== null
  const dirty = editing && draft !== (board.rules ?? '')

  function edit(text: string) {
    setDraft(text)
    setTab('write')
  }

  function leave(then: 'close' | 'cancel') {
    if (dirty) setConfirmDiscard(then)
    else if (then === 'close') onClose()
    else setDraft(null)
  }

  async function save() {
    if (draft === null) return
    setSaving(true)
    const ok = await onSave(draft)
    setSaving(false)
    if (ok) setDraft(null)
  }

  return (
    <>
      <Dialog title="Agent rules" subtitle={board.name} onClose={() => leave('close')} wide>
        <div className="dialog-body">
          <p className="dialog-hint">
            What this board is about and how agents should handle its notes. Agents read these over MCP and follow them in every
            action{owner ? '' : '; only owners can change them'}.
          </p>

          {editing ? (
            <section className="rules-editor">
              <div className="rules-tabs" role="tablist" aria-label="Editor">
                <button type="button" role="tab" aria-selected={tab === 'write'} onClick={() => setTab('write')}>
                  Write
                </button>
                <button type="button" role="tab" aria-selected={tab === 'preview'} onClick={() => setTab('preview')}>
                  Preview
                </button>
                <span className={`rules-count${draft.length > rulesMaxLength ? ' over' : ''}`}>
                  {draft.length.toLocaleString()} / {rulesMaxLength.toLocaleString()}
                </span>
              </div>
              {tab === 'write' ? (
                <textarea
                  className="rules-textarea"
                  aria-label="Agent rules in Markdown"
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                  maxLength={rulesMaxLength}
                  spellCheck
                  autoFocus
                />
              ) : (
                <RulesView text={draft} empty="Nothing to preview yet." />
              )}
              <div className="dialog-actions">
                <button type="button" className="ghost" onClick={() => leave('cancel')} disabled={saving}>
                  Cancel
                </button>
                <button type="button" className="primary" onClick={() => void save()} disabled={saving || !dirty}>
                  {saving ? 'Saving…' : 'Save rules'}
                </button>
              </div>
            </section>
          ) : board.rules ? (
            <section className="dialog-section">
              <RulesView text={board.rules} />
              <div className="rules-footer">
                {board.rulesUpdatedAt && (
                  <span className="dialog-hint">Last changed {new Date(board.rulesUpdatedAt).toLocaleString()}</span>
                )}
                {owner && (
                  <button type="button" className="ghost" onClick={() => edit(board.rules ?? '')}>
                    Edit rules
                  </button>
                )}
              </div>
            </section>
          ) : (
            <section className="dialog-section rules-empty">
              <p className="dialog-text">No rules yet.</p>
              {owner && (
                <div className="dialog-actions">
                  <button type="button" className="ghost" onClick={() => edit('')}>
                    Start blank
                  </button>
                  <button type="button" className="primary" onClick={() => edit(starterRules)}>
                    Write rules
                  </button>
                </div>
              )}
            </section>
          )}
        </div>
      </Dialog>

      {confirmDiscard && (
        <ConfirmDialog
          title="Discard your changes?"
          message="Your edits to the agent rules haven't been saved."
          confirmLabel="Discard"
          onConfirm={() => {
            setConfirmDiscard(null)
            setDraft(null)
            if (confirmDiscard === 'close') onClose()
          }}
          onCancel={() => setConfirmDiscard(null)}
        />
      )}
    </>
  )
}

/** The rules rendered as Markdown, or a hint when there is nothing yet. */
function RulesView({ text, empty }: { text: string; empty?: string }) {
  if (!text.trim()) return <p className="dialog-hint">{empty}</p>
  return <MarkdownView text={text} />
}
