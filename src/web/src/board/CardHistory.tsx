import { useEffect, useState, type ReactNode } from 'react'
import { api } from '../api/client'
import type { CardChange, CardChangeEntry, CardDetail } from '../api/types'
import { Avatar } from '../components/Avatar'
import { sinceDate } from './dates'
import { ChevronDownIcon, ChevronUpIcon } from './icons'

interface CardHistoryProps {
  card: CardDetail
  /** How many images and drawings are on it, so adding or removing one shows up while the history is open. */
  attached: number
}

/**
 * The card's history, in a sheet along the bottom of the open card. It starts folded to a bar, and the fold button
 * slides it up over the bottom of the canvas (which keeps its size). Loaded when it opens, and again whenever the card
 * changes while it is open.
 */
export function CardHistory({ card, attached }: CardHistoryProps) {
  const [open, setOpen] = useState(false)
  /** The changes, newest first; null while loading, 'failed' when they couldn't be loaded. */
  const [changes, setChanges] = useState<CardChange[] | null | 'failed'>(null)

  useEffect(() => {
    if (!open) return
    let current = true
    api.listCardHistory(card.id).then(
      (loaded) => current && setChanges(loaded),
      () => current && setChanges('failed'),
    )
    return () => {
      current = false
    }
  }, [open, card.id, card.updatedAt, card.deletedAt, attached])

  // Cards from before the history was kept still say when they were made.
  const recordsCreation = Array.isArray(changes) && changes.some((c) => c.entries.some((e) => e.kind === 'Created' || e.kind === 'Copied'))

  return (
    <section className={`card-history${open ? ' open' : ''}`} aria-label="History">
      <div className="card-history-sheet">
        <div className="card-history-head" onClick={() => setOpen(!open)}>
          <span>History</span>
          <button
            type="button"
            className="bucket-fold"
            aria-expanded={open}
            aria-label={open ? 'Fold the history away' : 'Show the history'}
            title={open ? 'Fold the history away' : 'Show the history'}
          >
            {open ? <ChevronDownIcon size={13} /> : <ChevronUpIcon size={13} />}
          </button>
        </div>
        {open && (
          <ol className="card-history-list">
            {changes === null && <li className="card-history-hint">Loading…</li>}
            {changes === 'failed' && <li className="card-history-hint">Couldn't load the history.</li>}
            {Array.isArray(changes) &&
              changes.map((change) => (
                <li key={change.id} className="card-history-change">
                  {change.by ? <Avatar assignee={change.by} /> : <span className="avatar avatar-sm" />}
                  <div className="card-history-what">
                    <div className="card-history-who">
                      <strong>{change.by?.name ?? 'Someone'}</strong>
                      <time dateTime={change.at} title={new Date(change.at).toLocaleString()}>
                        {sinceDate(change.at)}
                      </time>
                    </div>
                    {change.entries.map((entry, i) => (
                      <p key={i}>{describe(entry)}</p>
                    ))}
                  </div>
                </li>
              ))}
            {Array.isArray(changes) && !recordsCreation && (
              <li className="card-history-change">
                <span className="avatar avatar-sm" />
                <div className="card-history-what">
                  <div className="card-history-who">
                    <strong>{card.createdBy ?? 'Someone'}</strong>
                    <time dateTime={card.createdAt} title={new Date(card.createdAt).toLocaleString()}>
                      {sinceDate(card.createdAt)}
                    </time>
                  </div>
                  <p>created the card</p>
                </div>
              </li>
            )}
          </ol>
        )}
      </div>
    </section>
  )
}

const componentNames: Record<string, string> = { Text: 'note', Checklist: 'checklist', Links: 'list of links' }

/** A name or title in quotes. */
const q = (text: string) => <q>{text}</q>

const list = (items: string[]) => items.map((item, i) => [i > 0 && ', ', <b key={item}>{item}</b>])

/** One entry as a sentence after the person's name. */
function describe(entry: CardChangeEntry): ReactNode {
  const { before, after, added, removed } = entry
  switch (entry.kind) {
    case 'Created':
      return 'created the card'
    case 'Title':
      if (!before) return <>named it {q(after ?? '')}</>
      if (!after) return 'cleared the title'
      return (
        <>
          renamed it from {q(before)} to {q(after)}
        </>
      )
    case 'Color':
      return (
        <>
          changed the color from {before} to {after}
        </>
      )
    case 'Tags':
    case 'Assignees': {
      const [add, remove] = entry.kind === 'Tags' ? ['added', 'removed'] : ['assigned', 'unassigned']
      const noun = entry.kind === 'Tags' ? (n: number) => (n === 1 ? 'tag ' : 'tags ') : () => ''
      return (
        <>
          {added?.length ? (
            <>
              {add} {noun(added.length)}
              {list(added)}
            </>
          ) : null}
          {added?.length && removed?.length ? '; ' : null}
          {removed?.length ? (
            <>
              {remove} {noun(removed.length)}
              {list(removed)}
            </>
          ) : null}
        </>
      )
    }
    case 'AgentWorking':
      return after === 'true' ? 'started working on it' : 'finished working on it'
    case 'Bucket':
      return (
        <>
          moved it from {before ? <b>{before}</b> : 'the board'} to {after ? <b>{after}</b> : 'the board'}
        </>
      )
    case 'ComponentAdded':
    case 'ComponentChanged':
    case 'ComponentRemoved': {
      const verb = { ComponentAdded: 'added a', ComponentChanged: 'edited the', ComponentRemoved: 'removed the' }[entry.kind]
      return (
        <>
          {verb} {componentNames[before ?? ''] ?? 'component'}
          {after && <> {q(after)}</>}
        </>
      )
    }
    case 'ImageAdded':
      return <>attached an image{after && <> {q(after)}</>}</>
    case 'ImageRemoved':
      return <>removed an image{before && <> {q(before)}</>}</>
    case 'DrawingAdded':
      return 'added a drawing'
    case 'DrawingRemoved':
      return 'removed a drawing'

    case 'Deleted':
      return 'deleted it'
    case 'Restored':
      return 'put it back on the board'
    case 'Copied':
      return (
        <>
          copied it from{' '}
          {after ? (
            <a href={after} {...(after.startsWith('/') ? {} : { target: '_blank', rel: 'noreferrer' })}>
              {before ?? 'another card'}
            </a>
          ) : (
            <b>{before ?? 'another card'}</b>
          )}
        </>
      )
    default:
      // Other, or a kind added to the server after this app was built.
      return 'changed it'
  }
}
