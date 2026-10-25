import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api/client'
import type { SlackAppDetail, SlackAppState, SlackAppWorkspace } from '../api/types'
import { ConfirmDialog } from './ConfirmDialog'
import { plural } from './plural'

const stateLabel: Record<SlackAppState, string> = {
  Off: 'Off',
  Connecting: 'Connecting',
  Online: 'Online',
  Failed: 'Failed',
}

/**
 * The instance's Slack app, on the admin page next to the Discord bot: create it from the manifest PrikBoard fills in,
 * paste its two tokens and it connects over Socket Mode at once, and see where boards are previewed. Board owners then
 * pick channels under Integrations in the board menu.
 */
export function SlackAppSettings() {
  const [app, setApp] = useState<SlackAppDetail | null>(null)
  const [botToken, setBotToken] = useState('')
  const [appToken, setAppToken] = useState('')
  const [publicUrl, setPublicUrl] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState(false)
  const [forgetting, setForgetting] = useState<SlackAppWorkspace | null>(null)
  const [copied, setCopied] = useState(false)

  const reload = useCallback(() => api.getSlackApp().then(setApp, (e) => setError(errorMessage(e))), [])

  useEffect(() => {
    void reload()
  }, [reload])

  // An app that is still connecting is asked again shortly, so the status settles without reloading the page.
  useEffect(() => {
    if (app?.state !== 'Connecting') return
    const timer = window.setTimeout(() => void reload(), 3000)
    return () => window.clearTimeout(timer)
  }, [app, reload])

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
      botToken: botToken.trim() || undefined,
      appToken: appToken.trim() || undefined,
      publicUrl: publicUrl !== null && publicUrl.trim() !== (app?.publicUrl ?? '') ? publicUrl.trim() : undefined,
    }
    if (await change(() => api.setSlackApp(settings))) {
      setBotToken('')
      setAppToken('')
      setPublicUrl(null)
    }
  }

  async function copyManifest() {
    if (!app) return
    try {
      await navigator.clipboard.writeText(app.manifest)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      setError('Copying failed; select the manifest and copy it yourself.')
    }
  }

  if (app === null) {
    return error ? <p className="sign-in-error">{error}</p> : <p className="status-inline">Loading…</p>
  }

  const tokensFixed = app.source === 'Configuration'
  const firstTime = app.source === null
  // Before the app exists, the address can be saved on its own: the manifest takes its unfurl domain and redirect from it.
  const addressChanged = publicUrl !== null && publicUrl.trim() !== '' && publicUrl.trim() !== (app.publicUrl ?? '')
  const tokensGiven = botToken.trim() !== '' && appToken.trim() !== ''
  return (
    <div className="discord-bot">
      <p className="dialog-hint">
        An app that unfurls links to boards when someone posts them in Slack. It connects out to Slack (Socket Mode), so PrikBoard
        needs no address Slack can reach. Board owners pick the channels under Integrations in the board menu.
      </p>

      {app.state !== 'Off' && (
        <p className={`setup-check discord-bot-status ${app.state === 'Online' ? 'ok' : app.state === 'Failed' ? 'failed' : 'warning'}`}>
          <span className="setup-check-status">{stateLabel[app.state]}</span>
          <span>
            {app.botName ?? 'The app'}
            {app.state === 'Online' && app.teamName && ` is in ${app.teamName}`}
            {app.problem && `: ${app.problem}`}
          </span>
        </p>
      )}

      {firstTime && (
        <ol className="dialog-hint discord-bot-steps">
          <li>
            Check the address below, the one people open PrikBoard at (https, reachable from the internet), and save it: the manifest
            takes the app's link domain and sign-in redirect from it.
          </li>
          <li>
            <a href={app.createAppUrl} target="_blank" rel="noreferrer">
              Create the Slack app
            </a>{' '}
            from PrikBoard's manifest (or create one from a manifest yourself and paste the manifest below).
          </li>
          <li>Install it in your workspace, then copy the Bot User OAuth Token (xoxb-…) from OAuth &amp; Permissions.</li>
          <li>Under Basic Information → App-Level Tokens, create one with connections:write and copy it (xapp-…).</li>
          <li>Paste both below. Then add the app to channels with /invite @PrikBoard.</li>
        </ol>
      )}

      <details className="slack-manifest">
        <summary>Manifest</summary>
        <p className="dialog-hint">
          Its unfurl domain is the host of the address below: Slack only sends links on it. Change the address first if needed.
        </p>
        <pre>{app.manifest}</pre>
        <button type="button" className="ghost small" onClick={() => void copyManifest()}>
          {copied ? 'Copied' : 'Copy manifest'}
        </button>
      </details>

      <form className="token-form setup-form" onSubmit={(e) => void save(e)}>
        {!tokensFixed && (
          <>
            <label className="setup-field">
              <span>{firstTime ? 'Bot token' : 'New bot token'}</span>
              <input
                type="password"
                value={botToken}
                onChange={(e) => setBotToken(e.target.value)}
                placeholder={firstTime ? 'xoxb-… from OAuth & Permissions' : 'Leave empty to keep the stored one'}
                autoComplete="new-password"
              />
            </label>
            <label className="setup-field">
              <span>{firstTime ? 'App-level token' : 'New app-level token'}</span>
              <input
                type="password"
                value={appToken}
                onChange={(e) => setAppToken(e.target.value)}
                placeholder={firstTime ? 'xapp-… with connections:write' : 'Leave empty to keep the stored one'}
                autoComplete="new-password"
              />
            </label>
          </>
        )}
        <label className="setup-field">
          <span>Address people paste links from</span>
          <input
            value={publicUrl ?? app.publicUrl ?? ''}
            onChange={(e) => setPublicUrl(e.target.value)}
            placeholder={window.location.origin}
            disabled={app.publicUrlIsConfigured}
            title={app.publicUrlIsConfigured ? 'Set by Slack:PublicUrl in the server configuration' : undefined}
          />
        </label>
        {error && (
          <p className="sign-in-error" role="alert">
            {error}
          </p>
        )}
        <div className="dialog-actions">
          {app.source === 'Setup' && (
            <button type="button" className="ghost" onClick={() => setRemoving(true)}>
              Remove app
            </button>
          )}
          <button type="submit" className="primary" disabled={saving || (firstTime && !tokensGiven && !addressChanged)}>
            {firstTime && !tokensGiven ? 'Save address' : app.state === 'Off' ? 'Connect' : 'Save'}
          </button>
        </div>
      </form>

      {app.workspaces.length > 0 && (
        <ul className="row-list discord-bot-servers">
          {app.workspaces.map((w) => (
            <li key={w.teamId}>
              <span className="row-main">
                <span>
                  {w.name} <span className="row-meta">{plural(w.channelCount, 'channel', 'channels')}</span>
                </span>
                <span className="row-meta">
                  {w.boards.length === 0
                    ? 'No boards'
                    : w.boards.map((b) => `${b.boardName} (${b.wholeWorkspace ? 'whole workspace' : plural(b.channels.length, 'channel', 'channels')})`).join(', ')}
                </span>
              </span>
              {w.boards.length > 0 && (
                <button type="button" className="ghost small" onClick={() => setForgetting(w)}>
                  Disconnect boards
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {removing && (
        <ConfirmDialog
          title="Remove the Slack app?"
          message="The app disconnects and stops unfurling links. Boards stay connected to their workspace, so saving this app's tokens again brings their previews back. Uninstall the app in Slack too if you don't need it."
          confirmLabel="Remove"
          onConfirm={() => {
            setRemoving(false)
            void change(() => api.deleteSlackApp())
          }}
          onCancel={() => setRemoving(false)}
        />
      )}
      {forgetting && (
        <ConfirmDialog
          title={`Disconnect every board from "${forgetting.name}"?`}
          message="Links to these boards stop unfurling there. The app stays installed in Slack. Previews already posted stay in Slack."
          confirmLabel="Disconnect"
          onConfirm={() => {
            const target = forgetting
            setForgetting(null)
            void change(() => api.forgetSlackWorkspace(target.teamId))
          }}
          onCancel={() => setForgetting(null)}
        />
      )}
    </div>
  )
}
