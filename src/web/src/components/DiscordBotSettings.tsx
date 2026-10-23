import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api/client'
import type { DiscordBotDetail, DiscordBotServer, DiscordBotState } from '../api/types'
import { ConfirmDialog } from './ConfirmDialog'
import { plural } from './plural'

const stateLabel: Record<DiscordBotState, string> = {
  Off: 'Off',
  Connecting: 'Connecting',
  Online: 'Online',
  Failed: 'Failed',
}

/**
 * The instance's Discord bot, on the admin page: paste a bot token and it connects at once, invite it to servers, and
 * see where boards are previewed. Board owners then pick channels under Integrations in the board menu.
 */
export function DiscordBotSettings() {
  const [bot, setBot] = useState<DiscordBotDetail | null>(null)
  const [token, setToken] = useState('')
  const [publicUrl, setPublicUrl] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState(false)
  const [leaving, setLeaving] = useState<DiscordBotServer | null>(null)

  const reload = useCallback(() => api.getDiscordBot().then(setBot, (e) => setError(errorMessage(e))), [])

  useEffect(() => {
    void reload()
  }, [reload])

  // A bot that is still connecting is asked again shortly, so the status settles without reloading the page.
  useEffect(() => {
    if (bot?.state !== 'Connecting') return
    const timer = window.setTimeout(() => void reload(), 3000)
    return () => window.clearTimeout(timer)
  }, [bot, reload])

  async function change(action: () => Promise<unknown>) {
    setError(null)
    setSaving(true)
    try {
      await action()
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    } finally {
      setSaving(false)
      await reload()
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    const settings = {
      botToken: token.trim() || undefined,
      publicUrl: publicUrl !== null && publicUrl.trim() !== (bot?.publicUrl ?? '') ? publicUrl.trim() : undefined,
    }
    if (await change(() => api.setDiscordBot(settings))) {
      setToken('')
      setPublicUrl(null)
    }
  }

  if (bot === null) {
    return error ? <p className="sign-in-error">{error}</p> : <p className="status-inline">Loading…</p>
  }

  const tokenFixed = bot.source === 'Configuration'
  return (
    <div className="discord-bot">
      <p className="dialog-hint">
        A bot that previews links to boards when someone pastes them in Discord. Board owners pick the servers and channels under
        Integrations in the board menu.
      </p>

      {bot.state !== 'Off' && (
        <p className={`setup-check discord-bot-status ${bot.state === 'Online' ? 'ok' : bot.state === 'Failed' ? 'failed' : 'warning'}`}>
          <span className="setup-check-status">{stateLabel[bot.state]}</span>
          <span>
            {bot.botName ?? 'The bot'}
            {bot.state === 'Online' && ` is in ${plural(bot.servers.length, 'server', 'servers')}`}
            {bot.problem && `: ${bot.problem}`}
          </span>
        </p>
      )}
      {bot.isPublic && (
        <p className="dialog-hint">
          Anyone can add this bot to their server, and board owners here can then pick it. Turn off Public Bot on the Bot tab of
          your Discord application so only you can.
        </p>
      )}

      {bot.state === 'Off' && (
        <ol className="dialog-hint discord-bot-steps">
          <li>
            Create an application at{' '}
            <a href="https://discord.com/developers/applications" target="_blank" rel="noreferrer">
              discord.com/developers
            </a>
            .
          </li>
          <li>On its Bot tab, turn on Message Content Intent and turn off Public Bot.</li>
          <li>Click Reset Token there and paste the token below.</li>
        </ol>
      )}

      <form className="token-form setup-form" onSubmit={(e) => void save(e)}>
        {!tokenFixed && (
          <label className="setup-field">
            <span>{bot.source === 'Setup' ? 'New bot token' : 'Bot token'}</span>
            <input
              type="password"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder={bot.source === 'Setup' ? 'Leave empty to keep the stored one' : 'From the Bot tab of your application'}
              autoComplete="new-password"
            />
          </label>
        )}
        <label className="setup-field">
          <span>Address people paste links from</span>
          <input
            value={publicUrl ?? bot.publicUrl ?? ''}
            onChange={(e) => setPublicUrl(e.target.value)}
            placeholder={window.location.origin}
            disabled={bot.publicUrlIsConfigured}
            title={bot.publicUrlIsConfigured ? 'Set by Discord:PublicUrl in the server configuration' : undefined}
          />
        </label>
        {error && (
          <p className="sign-in-error" role="alert">
            {error}
          </p>
        )}
        <div className="dialog-actions">
          {bot.source === 'Setup' && (
            <button type="button" className="ghost" onClick={() => setRemoving(true)}>
              Remove bot
            </button>
          )}
          {bot.inviteUrl && (
            <a className="ghost" href={bot.inviteUrl} target="_blank" rel="noreferrer">
              Invite to a server
            </a>
          )}
          <button type="submit" className="primary" disabled={saving || (bot.state === 'Off' && !token.trim() && !tokenFixed)}>
            {bot.state === 'Off' ? 'Connect' : 'Save'}
          </button>
        </div>
      </form>

      {bot.servers.length > 0 && (
        <ul className="row-list discord-bot-servers">
          {bot.servers.map((s) => (
            <li key={s.guildId}>
              <span className="row-main">
                <span>
                  {s.name} <span className="row-meta">{plural(s.channelCount, 'channel', 'channels')}</span>
                </span>
                <span className="row-meta">
                  {s.boards.length === 0
                    ? 'No boards'
                    : s.boards.map((b) => `${b.boardName} (${b.channels === null ? 'whole server' : plural(b.channels.length, 'channel', 'channels')})`).join(', ')}
                </span>
              </span>
              <button type="button" className="ghost small" onClick={() => setLeaving(s)}>
                Leave
              </button>
            </li>
          ))}
        </ul>
      )}

      {removing && (
        <ConfirmDialog
          title="Remove the Discord bot?"
          message="The bot logs out and stops previewing links. Boards stay connected to their servers, so saving this bot's token again brings their previews back."
          confirmLabel="Remove"
          onConfirm={() => {
            setRemoving(false)
            void change(() => api.deleteDiscordBot())
          }}
          onCancel={() => setRemoving(false)}
        />
      )}
      {leaving && (
        <ConfirmDialog
          title={`Leave "${leaving.name}"?`}
          message="The bot leaves the server and every board connected to it is disconnected. Previews already posted stay in Discord."
          confirmLabel="Leave"
          onConfirm={() => {
            const target = leaving
            setLeaving(null)
            void change(() => api.leaveDiscordServer(target.guildId))
          }}
          onCancel={() => setLeaving(null)}
        />
      )}
    </div>
  )
}
