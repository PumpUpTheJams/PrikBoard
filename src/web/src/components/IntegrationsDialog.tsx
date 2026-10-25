import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardSummary, BucketDetail, DiscordChannel, DiscordConnection, DiscordServers, ChatUpdate } from '../api/types'
import { CloseIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Dialog } from './Dialog'
import { Select, type SelectOption } from './Select'
import { SlackIntegration } from './SlackIntegration'
import { updateChoices } from './chatUpdates'

/** The channel choice that previews the board in every channel of the server. */
const WHOLE_SERVER = '*'

/** The save bucket choice that means the board's first bucket. */
const FIRST_BUCKET = '*'

interface IntegrationsDialogProps {
  board: BoardSummary
  /** The board's buckets, for where Save as card puts cards. */
  buckets: BucketDetail[]
  /** Admins are pointed at the admin page to set the Discord bot up. */
  isAdmin: boolean
  onClose: () => void
}

/**
 * For owners: the apps this board is connected to, Discord and Slack ({@link SlackIntegration}). A board connects to channels of servers
 * the instance's bot is in, and/or to whole servers; links and readable ids preview there, and each connected channel
 * can also post the board's updates and decides where Save as card puts cards. An admin sets the bot up under
 * Admin → Instance.
 */
export function IntegrationsDialog({ board, buckets, isAdmin, onClose }: IntegrationsDialogProps) {
  const [connections, setConnections] = useState<DiscordConnection[] | null>(null)
  const [discord, setDiscord] = useState<DiscordServers | null>(null)
  const [guildId, setGuildId] = useState<string | null>(null)
  const [channelId, setChannelId] = useState<string | null>(null)
  const [editing, setEditing] = useState<{ guildId: string; channelId: string } | null>(null)
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

  /** Whether taking this away leaves the server with nothing on the board, which disconnects it (asked first). */
  const isLast = (c: DiscordConnection) => (c.wholeServer ? 1 : 0) + c.channels.length === 1

  const servers = discord?.servers ?? []
  const server = servers.find((s) => s.guildId === guildId) ?? servers[0]
  const connected = connections?.find((c) => c.guildId === server?.guildId)
  // Channels the board isn't connected to yet; "whole server" unless it already is.
  const channelOptions: SelectOption<string>[] = server
    ? [
        ...(connected?.wholeServer ? [] : [{ value: WHOLE_SERVER, label: 'Whole server', description: 'Links preview in every channel the bot can read' }]),
        ...server.channels
          .filter((c) => !connected?.channels.some((has) => has.id === c.id))
          .map((c) => ({ value: c.id, label: `#${c.name}` })),
      ]
    : []
  const channel = channelOptions.find((o) => o.value === channelId) ?? channelOptions.find((o) => o.value !== WHOLE_SERVER) ?? channelOptions[0]

  const editedConnection = editing && connections?.find((c) => c.guildId === editing.guildId)
  const edited = editedConnection?.channels.find((c) => c.id === editing?.channelId)

  return (
    <Dialog title="Integrations" subtitle={board.name} onClose={onClose} wide>
      <div className="dialog-body">
        <section className="dialog-section">
          <h3>Discord</h3>
          <p className="dialog-hint">
            Links to this board and its cards, and ids like {board.key}-12, show a preview in these channels, to everyone there,
            with titles, descriptions, tags and the people on cards. Previews keep up with the cards for a week.
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
                          {c.wholeServer && (
                            <span className="discord-channel">
                              Whole server
                              <button
                                type="button"
                                className="icon-button"
                                aria-label="Stop previewing in every channel"
                                title={isLast(c) ? 'Stop previewing in every channel (disconnects the server)' : 'Stop previewing in every channel'}
                                onClick={() => (isLast(c) ? setRemoving(c) : void attempt(() => api.removeDiscordWholeServer(board.id, c.guildId)))}
                              >
                                <CloseIcon size={12} />
                              </button>
                            </span>
                          )}
                          {c.channels.map((ch) => (
                            <ChannelChip
                              key={ch.id}
                              channel={ch}
                              open={editing?.guildId === c.guildId && editing.channelId === ch.id}
                              onOpen={() =>
                                setEditing((open) => (open?.guildId === c.guildId && open.channelId === ch.id ? null : { guildId: c.guildId, channelId: ch.id }))
                              }
                              onRemove={() =>
                                isLast(c) ? setRemoving(c) : void attempt(() => api.removeDiscordChannel(board.id, c.guildId, ch.id))
                              }
                            />
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

              {editedConnection && edited && (
                <ChannelSettings
                  key={`${editedConnection.guildId}:${edited.id}`}
                  channel={edited}
                  buckets={buckets}
                  busy={busy}
                  onChange={(settings) =>
                    void attempt(() => api.updateDiscordChannel(board.id, editedConnection.guildId, edited.id, settings))
                  }
                />
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
                        Connect
                      </button>
                    </>
                  ) : (
                    <span className="row-meta">Connected to every channel the bot can read</span>
                  )}
                </div>
              )}

              {connections.length > 0 && (
                <p className="dialog-hint discord-how">
                  Click a channel to have it post this board's updates. In Discord, people who can edit this board save a message as a
                  card with right-click → Apps → Save as card, and /prikboard board shows what is on it.
                </p>
              )}
            </>
          )}
          {error && (
            <p className="widget-error" role="alert">
              {error}
            </p>
          )}
        </section>
        <SlackIntegration board={board} buckets={buckets} isAdmin={isAdmin} />
      </div>

      {removing && (
        <ConfirmDialog
          title={`Disconnect "${removing.guildName}"?`}
          message="The bot stops previewing this board's links there and stops posting its updates. Previews already posted say the board is no longer available."
          confirmLabel="Disconnect"
          onConfirm={() => {
            const target = removing
            setRemoving(null)
            setEditing(null)
            void attempt(() => api.removeDiscordConnection(board.id, target.guildId))
          }}
          onCancel={() => setRemoving(null)}
        />
      )}
    </Dialog>
  )
}

interface ChannelChipProps {
  channel: DiscordChannel
  open: boolean
  onOpen: () => void
  onRemove: () => void
}

/** A connected channel: its name opens its settings, a bell shows it posts updates, the cross takes it away. */
function ChannelChip({ channel, open, onOpen, onRemove }: ChannelChipProps) {
  const posts = channel.updates.length > 0
  return (
    <span className={`discord-channel${open ? ' open' : ''}`}>
      <button
        type="button"
        className="discord-channel-name"
        aria-expanded={open}
        title={posts ? 'Posts this board’s updates. Click for settings' : 'Click for settings'}
        onClick={onOpen}
      >
        #{channel.name}
        {posts && <span aria-label="posts updates"> 🔔</span>}
      </button>
      <button type="button" className="icon-button" aria-label={`Disconnect #${channel.name}`} title="Stop previewing here" onClick={onRemove}>
        <CloseIcon size={12} />
      </button>
    </span>
  )
}

interface ChannelSettingsProps {
  channel: DiscordChannel
  buckets: BucketDetail[]
  busy: boolean
  onChange: (settings: { updates: ChatUpdate[]; saveBucketId: string | null }) => void
}

/** What one connected channel does for the board: the updates it posts, and where Save as card puts cards. */
function ChannelSettings({ channel, buckets, busy, onChange }: ChannelSettingsProps) {
  const bucketOptions: SelectOption<string>[] = [
    { value: FIRST_BUCKET, label: buckets[0] ? `${buckets[0].name} (first bucket)` : 'First bucket' },
    ...buckets.slice(1).map((b) => ({ value: b.id, label: b.name })),
  ]
  const saveBucket = channel.saveBucketId && buckets.some((b) => b.id === channel.saveBucketId) ? channel.saveBucketId : FIRST_BUCKET

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
            onChange={(e) =>
              onChange({
                updates: e.target.checked ? [...channel.updates, u.value] : channel.updates.filter((x) => x !== u.value),
                saveBucketId: channel.saveBucketId,
              })
            }
          />
          {u.label}
        </label>
      ))}
      <div className="discord-save-bucket">
        <span>Save as card puts cards in</span>
        <Select
          label="Bucket for saved messages"
          value={saveBucket}
          options={bucketOptions}
          disabled={busy}
          onChange={(value) => onChange({ updates: channel.updates, saveBucketId: value === FIRST_BUCKET ? null : value })}
        />
      </div>
    </div>
  )
}
