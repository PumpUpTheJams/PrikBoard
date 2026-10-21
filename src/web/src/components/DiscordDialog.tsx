import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { BoardSummary, DiscordConnection } from '../api/types'
import { CloseIcon } from '../board/icons'
import { ConfirmDialog } from './ConfirmDialog'
import { Dialog } from './Dialog'

interface DiscordDialogProps {
  board: BoardSummary
  onClose: () => void
}

/**
 * For owners: the Discord servers whose PrikBoard bot previews this board's links, and where. Servers are connected from
 * Discord (/prikboard connect), not here; here they are limited or removed.
 */
export function DiscordDialog({ board, onClose }: DiscordDialogProps) {
  const [connections, setConnections] = useState<DiscordConnection[] | null>(null)
  const [removing, setRemoving] = useState<DiscordConnection | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(
    () => api.listDiscordConnections(board.id).then(setConnections, (e) => setError(errorMessage(e))),
    [board.id],
  )

  useEffect(() => {
    void reload()
  }, [reload])

  async function attempt(action: () => Promise<unknown>) {
    setError(null)
    try {
      await action()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      await reload()
    }
  }

  return (
    <Dialog title="Discord" subtitle={board.name} onClose={onClose} wide>
      <div className="dialog-body">
        <section className="dialog-section">
          <h3>Servers previewing this board</h3>
          <p className="dialog-hint">
            When someone pastes a link to this board or one of its cards in these servers, the PrikBoard bot shows its title and
            description. To connect a server, an admin there runs /prikboard connect in a channel and you pick this board.
          </p>
          {connections === null ? (
            <p className="dialog-hint">Loading…</p>
          ) : connections.length === 0 ? (
            <p className="dialog-hint">No Discord servers are connected.</p>
          ) : (
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
                        c.channels.map((channel) => (
                          <span key={channel.id} className="discord-channel">
                            #{channel.name}
                            <button
                              type="button"
                              className="icon-button"
                              aria-label={`Stop previewing in #${channel.name}`}
                              title={c.channels!.length === 1 ? 'Stop previewing here (disconnects the server)' : 'Stop previewing here'}
                              onClick={() =>
                                c.channels!.length === 1
                                  ? setRemoving(c)
                                  : void attempt(() => api.removeDiscordChannel(board.id, c.guildId, channel.id))
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
