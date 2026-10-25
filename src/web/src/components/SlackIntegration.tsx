import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardSummary, SlackConnection, SlackWorkspaces } from '../api/types'
import { CloseIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Select, type SelectOption } from './Select'

/** The channel choice that previews the board in every channel of the workspace. */
const WHOLE_WORKSPACE = '*'

interface SlackIntegrationProps {
  board: BoardSummary
  /** Admins are pointed at the admin page to set the Slack app up. */
  isAdmin: boolean
}

/**
 * The Slack part of Integrations: the board connects to channels of the workspace the instance's Slack app is in, or
 * to the whole workspace, and links to it unfurl there. Unlike Discord, channels have no settings yet (updates and Save
 * to PrikBoard come later, docs/slack.md), so a channel is just a chip.
 */
export function SlackIntegration({ board, isAdmin }: SlackIntegrationProps) {
  const [connections, setConnections] = useState<SlackConnection[] | null>(null)
  const [slack, setSlack] = useState<SlackWorkspaces | null>(null)
  const [teamId, setTeamId] = useState<string | null>(null)
  const [channelId, setChannelId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [removing, setRemoving] = useState<SlackConnection | null>(null)
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

  return (
    <section className="dialog-section">
      <h3>Slack</h3>
      <p className="dialog-hint">
        Links to this board and its cards unfurl into a preview in these channels, for everyone there, with titles, descriptions,
        tags and the people on cards. Never in channels shared with other organizations.
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
                      {c.channels.map((ch) => (
                        <span key={ch.id} className="discord-channel">
                          <span className="discord-channel-name">#{ch.name}</span>
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
                      ))}
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
              A channel shows up here once the app is in it: in Slack, type <code>/invite @PrikBoard</code> in that channel.
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
          message="Links to this board stop unfurling there. Previews already posted stay in Slack."
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
