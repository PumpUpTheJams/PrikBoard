import { useState } from 'react'
import { api } from '../api/client'
import type { BoardDetail, CardChanges, CardComponent, CardComponentChanges, CardComponentKind, CardDetail, ImageDetail } from '../api/types'
import { AssigneePicker } from '../board/AssigneePicker'
import { BoardImageView } from '../board/BoardImageView'
import { ChecklistComponent, ComponentFrame, LinksComponent, TextComponent } from '../board/CardComponents'
import { componentKinds } from '../board/componentKinds'
import { EditableText } from '../board/EditableText'
import { CardIdButton, FillPicker, TagChips } from '../board/ExpandedCard'
import { pickImageFiles } from '../board/files'
import { BucketIcon, ChevronDownIcon, ChevronLeftIcon, CloseIcon, ImageIcon, SparkleIcon, TrashIcon } from '../board/icons'
import { belowAll, defaultComponentSize, readingOrder } from '../board/layout'
import { bucketPath, cardLabel, imagePath, TITLE_SUGGESTION } from '../board/links'
import { ops } from '../board/ops'
import { MAX_ASSIGNEES } from '../board/tags'
import type { TagColors } from '../board/tagColors'
import { TagPicker } from '../board/TagPicker'
import { created, snap, step, target, type RecordingRun } from '../board/undo'
import { AvatarStack } from '../components/Avatar'
import { ComponentEditor, type ComponentDraft } from './ComponentEditor'
import { bucketItems, bucketsInOrder } from './items'
import { back, navigate } from './nav'
import { AddButton, Sheet } from './parts'

/** Room taken by an image that is still uploading, to keep the next one below it. */
const UPLOAD_SIZE = { width: 320, height: 240 }

interface CardScreenProps {
  board: BoardDetail
  card: CardDetail
  /** Just made: its title is focused to type straight away. */
  fresh: boolean
  tagColors: TagColors
  run: RecordingRun
  notify: (text: string) => void
}

type Picking = { kind: 'tags' | 'people'; anchor: DOMRect }

/**
 * The fourth mobile screen: a card, with the same band as the expanded card on the desktop (fill, title, tags, people,
 * readable id) and its components and attached images stacked below in reading order. One button adds to it.
 */
export function CardScreen({ board, card, fresh, tagColors, run, notify }: CardScreenProps) {
  const readOnly = board.yourRole === 'Viewer'
  const [picking, setPicking] = useState<Picking | null>(null)
  const [sheet, setSheet] = useState<'add' | 'move' | null>(null)
  /** The editor open over the card: a new component of a kind, or an existing one. */
  const [editing, setEditing] = useState<{ kind: CardComponentKind; component?: CardComponent } | null>(null)
  const bucket = board.buckets.find((b) => b.id === card.bucketId)
  const up = bucketPath(board.shortId, card.bucketId)
  const images = board.images.filter((i) => i.cardId === card.id)
  const stack = readingOrder([
    ...card.components.map((component) => ({ component, image: null, x: component.x, y: component.y })),
    ...images.map((image) => ({ component: null, image, x: image.x, y: image.y })),
  ])

  const edit = (changes: CardChanges) => run(...ops.editCard(card, changes))
  const saveComponent = (component: CardComponent, changes: CardComponentChanges) => run(...ops.editComponent(card.id, component, changes))

  /** Where something new goes: below everything on the card's canvas, so it is last in the list too. */
  const spotFor = (size: { width: number; height: number }, also: ImageDetail[] = []) =>
    belowAll([...card.components, ...images, ...also], size)

  async function addComponent(draft: ComponentDraft) {
    const [add, describe] = ops.addComponent(card.id, { ...draft, ...spotFor(defaultComponentSize(draft.kind)) })
    if (await run(add, describe)) setEditing(null)
  }

  /** Saves what changed in the editor, as one step. */
  async function saveEdits(component: CardComponent, draft: ComponentDraft) {
    const changes: CardComponentChanges = {}
    if ((draft.title ?? '') !== (component.title ?? '')) changes.title = draft.title ?? ''
    if (component.kind === 'Text' && (draft.text ?? '') !== (component.text ?? '')) changes.text = draft.text ?? ''
    if (component.kind === 'Checklist' && JSON.stringify(draft.items) !== JSON.stringify(component.items)) changes.items = draft.items ?? []
    if (component.kind === 'Links' && JSON.stringify(draft.links) !== JSON.stringify(component.links)) changes.links = draft.links ?? []
    if (Object.keys(changes).length === 0 || (await saveComponent(component, changes))) setEditing(null)
  }

  async function removeComponent(component: CardComponent) {
    setEditing(null)
    await run(...ops.removeComponent(card.id, component))
  }

  async function addImages() {
    const files = await pickImageFiles()
    if (files.length === 0) return
    notify(files.length === 1 ? 'Uploading the image…' : `Uploading ${files.length} images…`)
    const added: ImageDetail[] = []
    await run(
      async () => {
        for (const file of files) added.push(await api.uploadImage(board.id, file, { cardId: card.id, ...spotFor(UPLOAD_SIZE, added) }))
        return added
      },
      (list) => step(list.length === 1 ? 'Add image' : 'Add images', list.map((image) => created(target.image(image.id), snap.image(image)))),
    )
  }

  async function remove() {
    if (await run(...ops.deleteCard(card))) back(up)
  }

  async function moveTo(bucketId: string) {
    setSheet(null)
    if (bucketId === card.bucketId) return
    const name = board.buckets.find((b) => b.id === bucketId)?.name ?? 'the bucket'
    await run(...ops.moveCardInto(card, bucketId, `Moved to ${name}`))
  }

  if (editing) {
    const { kind, component } = editing
    return (
      <ComponentEditor
        kind={kind}
        card={card}
        cards={board.cards}
        component={component}
        onCancel={() => setEditing(null)}
        onSave={(draft) => (component ? saveEdits(component, draft) : addComponent(draft))}
        onRemove={component && (() => void removeComponent(component))}
      />
    )
  }

  return (
    <div className="m-screen m-card-screen">
      <header className={`band m-band fill-${card.color.toLowerCase()}`}>
        <div className="band-row m-band-top">
          <button type="button" className="m-back" aria-label={`Back to ${bucket?.name ?? 'the board'}`} onClick={() => back(up)}>
            <ChevronLeftIcon />
          </button>
          <button type="button" className="band-button m-band-bucket" disabled={readOnly} onClick={() => setSheet('move')}>
            <BucketIcon />
            {bucket ? bucket.name : 'Not in a bucket'}
            {!readOnly && <ChevronDownIcon />}
          </button>
          {!readOnly && <FillPicker value={card.color} onChange={(color) => void edit({ color })} />}
          {!readOnly && (
            <button type="button" className="band-button icon" aria-label="Delete card" title="Delete card" onClick={() => void remove()}>
              <TrashIcon size={17} />
            </button>
          )}
        </div>
        <EditableText
          className="band-title"
          label="Title"
          value={card.title}
          maxLength={200}
          multiline
          placeholder={TITLE_SUGGESTION}
          autoFocus={fresh && !readOnly}
          readOnly={readOnly}
          onSave={(title) => void edit({ title: title.replace(/\s+/g, ' ').trim() })}
        />
        {readOnly ? (
          card.tags.length > 0 && <div className="band-tags">{card.tags.map((t) => <span key={t} className="band-tag">{t}</span>)}</div>
        ) : (
          <TagChips
            tags={card.tags}
            tagColors={tagColors}
            onRemove={(tag) => void edit({ tags: card.tags.filter((t) => t !== tag) })}
            onAdd={(anchor) => setPicking({ kind: 'tags', anchor })}
          />
        )}
        <div className="band-row m-band-people">
          {card.assignees.length > 0 && (
            <button
              type="button"
              className="m-people"
              aria-label={`People: ${card.assignees.map((a) => a.name).join(', ')}`}
              title={card.assignees.map((a) => a.name).join(', ')}
              disabled={readOnly}
              onClick={(e) => setPicking({ kind: 'people', anchor: e.currentTarget.getBoundingClientRect() })}
            >
              <AvatarStack assignees={card.assignees} max={5} size="md" />
            </button>
          )}
          {!readOnly && (
            <button type="button" className="band-add" aria-haspopup="dialog" onClick={(e) => setPicking({ kind: 'people', anchor: e.currentTarget.getBoundingClientRect() })}>
              {card.assignees.length < MAX_ASSIGNEES ? '+ assign' : 'people'}
            </button>
          )}
        </div>
        <div className="band-row band-meta">
          {card.agentWorking ? (
            <button type="button" className="agent-chip" disabled={readOnly} title="The agent has finished: clear the halo" onClick={() => void edit({ agentWorking: false })}>
              <SparkleIcon />
              Agent working
              {!readOnly && <CloseIcon size={12} />}
            </button>
          ) : (
            <span>{card.createdBy ? `Created by ${card.createdBy}` : ''}</span>
          )}
          <CardIdButton card={card} />
        </div>
      </header>

      <div className="m-scroll m-stack">
        {stack.map(({ component, image }) =>
          component ? (
            // Shown, not edited, here: its edit button opens it in the editor. Checklist items can still be ticked off.
            <ComponentFrame
              key={component.id}
              component={component}
              selected={false}
              lifted={false}
              readOnly
              onSelect={() => {}}
              onTitle={() => {}}
              onRemove={() => {}}
              onEdit={readOnly ? undefined : () => setEditing({ kind: component.kind, component })}
            >
              {component.kind === 'Text' && <TextComponent text={component.text ?? ''} autoFocus={false} readOnly onChange={() => {}} />}
              {component.kind === 'Checklist' && (
                <ChecklistComponent
                  items={component.items ?? []}
                  autoFocus={false}
                  readOnly={readOnly}
                  tickOnly
                  onChange={(items) => saveComponent(component, { items })}
                />
              )}
              {component.kind === 'Links' && (
                <LinksComponent links={component.links ?? []} card={card} cards={board.cards} autoFocus={false} readOnly onChange={() => Promise.resolve(false)} />
              )}
            </ComponentFrame>
          ) : (
            image && (
              <BoardImageView
                key={image.id}
                image={image}
                selected={false}
                lifted={false}
                onOpen={() => navigate(imagePath(board.shortId, image.id))}
              />
            )
          ),
        )}
        {stack.length === 0 && <p className="m-empty">{readOnly ? 'Nothing on this card yet.' : 'Nothing on this card yet. Add a note, a checklist, links or an image.'}</p>}
      </div>

      {!readOnly && <AddButton label="Add" onClick={() => setSheet('add')} />}

      {sheet === 'add' && (
        <Sheet title="Add to the card" aside={<span className="m-sheet-aside">{card.readableId}</span>} onClose={() => setSheet(null)}>
          <ul className="m-list">
            {(['Text', 'Checklist', 'Links'] as const).map((kind) => {
              const { label, Icon } = componentKinds[kind]
              return (
                <li key={kind}>
                  <button
                    type="button"
                    className="m-row"
                    onClick={() => {
                      setSheet(null)
                      setEditing({ kind })
                    }}
                  >
                    <span className="m-row-icon">
                      <Icon size={18} />
                    </span>
                    <span className="m-row-text">
                      <span className="m-row-name">{label}</span>
                      <span className="m-row-detail">{kindHint[kind]}</span>
                    </span>
                  </button>
                </li>
              )
            })}
            <li>
              <button
                type="button"
                className="m-row"
                onClick={() => {
                  setSheet(null)
                  void addImages()
                }}
              >
                <span className="m-row-icon">
                  <ImageIcon />
                </span>
                <span className="m-row-text">
                  <span className="m-row-name">Image</span>
                  <span className="m-row-detail">Take a photo or pick one</span>
                </span>
              </button>
            </li>
          </ul>
          <p className="m-note">New items go at the bottom of the card.</p>
        </Sheet>
      )}

      {sheet === 'move' && (
        <Sheet title="Move to" aside={<span className="m-sheet-aside">{card.readableId}</span>} onClose={() => setSheet(null)}>
          <ul className="m-list m-choices" role="radiogroup" aria-label={`Bucket for ${cardLabel(card)}`}>
            {bucketsInOrder(board).map((b) => (
              <li key={b.id}>
                <button type="button" className="m-choice" role="radio" aria-checked={b.id === card.bucketId} onClick={() => void moveTo(b.id)}>
                  <span className="m-radio" aria-hidden="true" />
                  <span className="m-row-name">{b.name}</span>
                  {b.collapsed && <span className="m-row-detail">Collapsed</span>}
                  <span className="count">{bucketItems(board, b.id).filter((i) => i.kind === 'card').length}</span>
                </button>
              </li>
            ))}
          </ul>
          <p className="m-note">It goes into the first free spot; a full bucket grows downwards.</p>
        </Sheet>
      )}

      {picking?.kind === 'tags' && (
        <TagPicker
          anchor={picking.anchor}
          tags={card.tags}
          boardTags={board.tags}
          tagColors={tagColors}
          onChange={(tags) => edit({ tags })}
          onRename={(tag, name) => run(...ops.renameTag(board.id, tag, name))}
          onRecolor={(tag, color) => run(...ops.recolorTag(board.id, tag, color))}
          onPreview={() => {}}
          onDelete={(tag) => run(...ops.deleteTag(board.id, tag))}
          onClose={() => setPicking(null)}
        />
      )}
      {picking?.kind === 'people' && (
        <AssigneePicker
          anchor={picking.anchor}
          assignees={card.assignees}
          people={board.people}
          onChange={(assigneeIds) => edit({ assigneeIds })}
          onClose={() => setPicking(null)}
        />
      )}
    </div>
  )
}

const kindHint: Record<CardComponentKind, string> = {
  Text: 'Text in Markdown',
  Checklist: 'Items to tick off',
  Links: 'Web pages or other cards',
}
