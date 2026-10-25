import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardSummary, ChatUpdate, SlackChannel, SlackConnection, SlackWorkspaces } from '../api/types'
import { CloseIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Select, type SelectOption } from './Select'
import { updateChoices } from './chatUpdates'

/** The channel choice that previews the board in every channel of the workspace. */
const WHOLE_WORKSPACE = '*'

interface SlackIntegrationProps {
  board: BoardSummary
  /** Admins are pointed at the admin page to set the Slack app up. */
  isAdmin: boolean
}

/**
 * The Slack part of Integrations: the board connects to channels of the workspace the instance's Slack app is in, or
 * to the whole workspace, and links to it unfurl there. A channel's chip opens its settings: the board's updates it
 * posts. (Save to PrikBoard, and so a save bucket, comes later; docs/slack.md.)
 */
export function SlackIntegration({ board, isAdmin }: SlackIntegrationProps) {
  const [connections, setConnections] = useState<SlackConnection[] | null>(null)
  const [slack, setSlack] = useState<SlackWorkspaces | null>(null)
  const [teamId, setTeamId] = useState<string | null>(null)
  const [channelId, setChannelId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [removing, setRemoving] = useState<SlackConnection | null>(null)
  const [editing, setEditing] = useState<{ teamId: string; channelId: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(
    () =>
      Promise.all([api.listSlackConnections(board.id), api.listSlackWorkspaces(board.id)]).then(([c, w]) => {
        setConnections(c)
        setSlack(w)
      }, (e) => setError(errorMessage(e))),
    [board.id],
  )

  useEffect(() => {
    void reload()
  }, [reload])

  async function attempt(action: () => Promise<unknown>) {
    setError(null)
    setBusy(true)
    try {
      await action()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
      await reload()
    }
  }

  /** Whether taking this away leaves the workspace with nothing on the board, which disconnects it (asked first). */
  const isLast = (c: SlackConnection) => (c.wholeWorkspace ? 1 : 0) + c.channels.length === 1

  const workspaces = slack?.workspaces ?? []
  const workspace = workspaces.find((w) => w.teamId === teamId) ?? workspaces[0]
  const connected = connections?.find((c) => c.teamId === workspace?.teamId)
  const channelOptions: SelectOption<string>[] = workspace
    ? [
        ...(connected?.wholeWorkspace ? [] : [{ value: WHOLE_WORKSPACE, label: 'Whole workspace', description: 'Links unfurl in every channel' }]),
        ...workspace.channels
          .filter((c) => !connected?.channels.some((has) => has.id === c.id))
          .map((c) => ({ value: c.id, label: `#${c.name}` })),
      ]
    : []
  const channel = channelOptions.find((o) => o.value === channelId) ?? channelOptions.find((o) => o.value !== WHOLE_WORKSPACE) ?? channelOptions[0]

  const editedConnection = editing && connections?.find((c) => c.teamId === editing.teamId)
  const edited = editedConnection?.channels.find((c) => c.id === editing?.channelId)

  return (
    <section className="dialog-section">
      <h3>Slack</h3>
      <p className="dialog-hint">
        Links to this board and its cards unfurl into a preview in these channels, for everyone there, with titles, descriptions,
        tags and the people on cards. Previews keep up with the cards for a week. Never in channels shared with other organizations.
      </p>
      {connections === null || slack === null ? (
        <p className="dialog-hint">Loading…</p>
      ) : (
        <>
          {connections.length > 0 && (
            <ul className="row-list">
              {connections.map((c) => (
                <li key={c.teamId}>
                  <span className="row-main token-row">
                    <span>
                      {c.teamName} <span className="badge slack">Slack</span>
                    </span>
                    <span className="discord-channels">
                      {c.wholeWorkspace && (
                        <span className="discord-channel">
                          Whole workspace
                          <button
                            type="button"
                            className="icon-button"
                            aria-label="Stop unfurling in every channel"
                            title={isLast(c) ? 'Stop unfurling in every channel (disconnects the workspace)' : 'Stop unfurling in every channel'}
                            onClick={() => (isLast(c) ? setRemoving(c) : void attempt(() => api.removeSlackWholeWorkspace(board.id, c.teamId)))}
                          >
                            <CloseIcon size={12} />
                          </button>
                        </span>
                      )}
                      {c.channels.map((ch) => {
                        const open = editing?.teamId === c.teamId && editing.channelId === ch.id
                        const posts = ch.updates.length > 0
                        return (
                          <span key={ch.id} className={`discord-channel${open ? ' open' : ''}`}>
                            <button
                              type="button"
                              className="discord-channel-name"
                              aria-expanded={open}
                              title={posts ? 'Posts this board’s updates. Click for settings' : 'Click for settings'}
                              onClick={() => setEditing(open ? null : { teamId: c.teamId, channelId: ch.id })}
                            >
                              #{ch.name}
                              {posts && <span aria-label="posts updates"> 🔔</span>}
                            </button>
                            <button
                              type="button"
                              className="icon-button"
                              aria-label={`Disconnect #${ch.name}`}
                              title="Stop unfurling here"
                              onClick={() => (isLast(c) ? setRemoving(c) : void attempt(() => api.removeSlackChannel(board.id, c.teamId, ch.id)))}
                            >
                              <CloseIcon size={12} />
                            </button>
                          </span>
                        )
                      })}
                    </span>
                    <span className="row-meta">
                      Connected {new Date(c.connectedAt).toLocaleDateString()}
                      {c.connectedBy ? ` · by ${c.connectedBy}` : ''}
                    </span>
                  </span>
                  <button type="button" className="ghost small" onClick={() => setRemoving(c)}>
                    Disconnect
                  </button>
                </li>
              ))}
            </ul>
          )}

          {editedConnection && edited && (
            <SlackChannelSettings
              key={`${editedConnection.teamId}:${edited.id}`}
              channel={edited}
              busy={busy}
              onChange={(updates) => void attempt(() => api.updateSlackChannel(board.id, editedConnection.teamId, edited.id, { updates }))}
            />
          )}

          {slack.state === 'Off' ? (
            <p className="dialog-hint">
              {isAdmin ? (
                <>
                  Set up the Slack app first, under <a href="#/admin">Admin</a> → Instance.
                </>
              ) : (
                'No Slack app is set up yet. Ask an admin of this PrikBoard to add one.'
              )}
            </p>
          ) : workspaces.length === 0 ? (
            <p className="dialog-hint">The Slack app isn't online right now, so its channels can't be listed. Try again in a minute.</p>
          ) : (
            <div className="discord-add">
              {workspaces.length > 1 && (
                <Select
                  label="Slack workspace"
                  value={workspace!.teamId}
                  options={workspaces.map((w) => ({ value: w.teamId, label: w.name }))}
                  onChange={(id) => {
                    setTeamId(id)
                    setChannelId(null)
                  }}
                />
              )}
              {channel ? (
                <>
                  <Select label="Channel" value={channel.value} options={channelOptions} onChange={setChannelId} />
                  <button
                    type="button"
                    className="primary small"
                    disabled={busy}
                    onClick={() =>
                      void attempt(() => api.connectSlackChannel(board.id, workspace!.teamId, channel.value === WHOLE_WORKSPACE ? null : channel.value))
                    }
                  >
                    Connect
                  </button>
                </>
              ) : (
                <span className="row-meta">Connected to every channel</span>
              )}
            </div>
          )}

          {slack.state !== 'Off' && workspace && (
            <p className="dialog-hint discord-how">
              A channel shows up here once the app is in it: in Slack, type <code>/invite @PrikBoard</code> in that channel. Click a
              connected channel to have it post this board's updates; <code>/prikboard board</code> there shows what is on the board.
            </p>
          )}
        </>
      )}
      {error && (
        <p className="widget-error" role="alert">
          {error}
        </p>
      )}

      {removing && (
        <ConfirmDialog
          title={`Disconnect "${removing.teamName}"?`}
          message="Links to this board stop unfurling there, and its updates stop. Previews posted in the last week say the board is no longer available."
          confirmLabel="Disconnect"
          onConfirm={() => {
            const target = removing
            setRemoving(null)
            void attempt(() => api.removeSlackConnection(board.id, target.teamId))
          }}
          onCancel={() => setRemoving(null)}
        />
      )}
    </section>
  )
}

interface SlackChannelSettingsProps {
  channel: SlackChannel
  busy: boolean
  onChange: (updates: ChatUpdate[]) => void
}

/** What one connected Slack channel does for the board: the updates it posts. */
function SlackChannelSettings({ channel, busy, onChange }: SlackChannelSettingsProps) {
  return (
    <div className="discord-channel-settings">
      <h4>#{channel.name}</h4>
      <p className="dialog-hint">Post here as it happens on the board (gathered for half a minute, one post per board):</p>
      {updateChoices.map((u) => (
        <label key={u.value} className="check-row">
          <input
            type="checkbox"
            checked={channel.updates.includes(u.value)}
            disabled={busy}
            onChange={(e) => onChange(e.target.checked ? [...channel.updates, u.value] : channel.updates.filter((x) => x !== u.value))}
          />
          {u.label}
        </label>
      ))}
    </div>
  )
}
