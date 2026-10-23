import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardSummary, DiscordConnection, DiscordServers } from '../api/types'
import { CloseIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Dialog } from './Dialog'
import { Select, type SelectOption } from './Select'

/** The channel choice that previews the board in every channel of the server. */
const WHOLE_SERVER = '*'

interface IntegrationsDialogProps {
  board: BoardSummary
  /** Admins are pointed at the admin page to set the Discord bot up. */
  isAdmin: boolean
  onClose: () => void
}

/**
 * For owners: the apps this board is connected to. For now that is Discord: the servers and channels where this
 * PrikBoard's bot previews the board's links. Owners pick them here from the servers the bot is in; an admin sets the
 * bot up under Admin → Instance.
 */
export function IntegrationsDialog({ board, isAdmin, onClose }: IntegrationsDialogProps) {
  const [connections, setConnections] = useState<DiscordConnection[] | null>(null)
  const [discord, setDiscord] = useState<DiscordServers | null>(null)
  const [guildId, setGuildId] = useState<string | null>(null)
  const [channelId, setChannelId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [removing, setRemoving] = useState<DiscordConnection | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(
    () =>
      Promise.all([api.listDiscordConnections(board.id), api.listDiscordServers(board.id)]).then(([c, s]) => {
        setConnections(c)
        setDiscord(s)
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

  const servers = discord?.servers ?? []
  const server = servers.find((s) => s.guildId === guildId) ?? servers[0]
  const connected = connections?.find((c) => c.guildId === server?.guildId)
  // Channels the board isn't previewed in yet; "whole server" unless it already is.
  const channelOptions: SelectOption<string>[] = server
    ? [
        ...(connected?.channels === null
          ? []
          : [{ value: WHOLE_SERVER, label: 'Whole server', description: 'Every channel the bot can read' }]),
        ...server.channels
          .filter((c) => connected?.channels === null || !connected?.channels.some((has) => has.id === c.id))
          .map((c) => ({ value: c.id, label: `#${c.name}` })),
      ]
    : []
  const channel = channelOptions.find((o) => o.value === channelId) ?? channelOptions.find((o) => o.value !== WHOLE_SERVER) ?? channelOptions[0]

  return (
    <Dialog title="Integrations" subtitle={board.name} onClose={onClose} wide>
      <div className="dialog-body">
        <section className="dialog-section">
          <h3>Discord</h3>
          <p className="dialog-hint">
            When someone pastes a link to this board or one of its cards in these channels, PrikBoard's Discord bot shows its
            title and description to everyone there.
          </p>
          {connections === null || discord === null ? (
            <p className="dialog-hint">Loading…</p>
          ) : (
            <>
              {connections.length > 0 && (
                <ul className="row-list">
                  {connections.map((c) => (
                    <li key={c.guildId}>
                      <span className="row-main token-row">
                        <span>
                          {c.guildName} <span className="badge discord">Discord</span>
                        </span>
                        <span className="discord-channels">
                          {c.channels === null ? (
                            <span className="row-meta">Whole server</span>
                          ) : (
                            c.channels.map((ch) => (
                              <span key={ch.id} className="discord-channel">
                                #{ch.name}
                                <button
                                  type="button"
                                  className="icon-button"
                                  aria-label={`Stop previewing in #${ch.name}`}
                                  title={c.channels!.length === 1 ? 'Stop previewing here (disconnects the server)' : 'Stop previewing here'}
                                  onClick={() =>
                                    c.channels!.length === 1
                                      ? setRemoving(c)
                                      : void attempt(() => api.removeDiscordChannel(board.id, c.guildId, ch.id))
                                  }
                                >
                                  <CloseIcon size={12} />
                                </button>
                              </span>
                            ))
                          )}
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

              {discord.state === 'Off' ? (
                <p className="dialog-hint">
                  {isAdmin ? (
                    <>
                      Set up a Discord bot first, under <a href="#/admin">Admin</a> → Instance.
                    </>
                  ) : (
                    'No Discord bot is set up yet. Ask an admin of this PrikBoard to add one.'
                  )}
                </p>
              ) : servers.length === 0 ? (
                <p className="dialog-hint">
                  {discord.state === 'Online'
                    ? `The bot isn't in any Discord server yet. ${isAdmin ? 'Invite it from Admin → Instance.' : 'Ask an admin of this PrikBoard to invite it to yours.'}`
                    : "The Discord bot isn't online right now, so its servers can't be listed. Try again in a minute."}
                </p>
              ) : (
                <div className="discord-add">
                  <Select
                    label="Discord server"
                    value={server!.guildId}
                    options={servers.map((s) => ({ value: s.guildId, label: s.name }))}
                    onChange={(id) => {
                      setGuildId(id)
                      setChannelId(null)
                    }}
                  />
                  {channel ? (
                    <>
                      <Select label="Channel" value={channel.value} options={channelOptions} onChange={setChannelId} />
                      <button
                        type="button"
                        className="primary small"
                        disabled={busy}
                        onClick={() =>
                          void attempt(() =>
                            api.connectDiscordChannel(board.id, server!.guildId, channel.value === WHOLE_SERVER ? null : channel.value),
                          )
                        }
                      >
                        {connected?.channels === null ? 'Limit to channel' : 'Connect'}
                      </button>
                    </>
                  ) : (
                    <span className="row-meta">Previewed in every channel the bot can read</span>
                  )}
                </div>
              )}
            </>
          )}
          {error && (
            <p className="widget-error" role="alert">
              {error}
            </p>
          )}
        </section>
      </div>

      {removing && (
        <ConfirmDialog
          title={`Disconnect "${removing.guildName}"?`}
          message="The bot stops previewing this board's links there. Previews already posted in Discord stay there. Delete them in Discord if needed."
          confirmLabel="Disconnect"
          onConfirm={() => {
            const target = removing
            setRemoving(null)
            void attempt(() => api.removeDiscordConnection(board.id, target.guildId))
          }}
          onCancel={() => setRemoving(null)}
        />
      )}
    </Dialog>
  )
}
