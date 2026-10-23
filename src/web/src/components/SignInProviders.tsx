import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage, testSignInUrl } from '../api/client'
import type { CreateSignInProvider, GitHubAppManifest, ProviderType, SignInProviderDetail } from '../api/types'
import { ExternalIcon, PencilIcon, TrashIcon } from '../board/icons'
import { useCopy } from '../board/useCopy'
import { ConfirmDialog } from './ConfirmDialog'
import { CopyField } from './CopyField'
import { plural } from './plural'
import { takeSetupResult } from './setupResult'

/** The kinds of provider the setup offers, each with its own form. */
type Template = 'github-app' | 'github' | 'google' | 'discord' | 'steam' | 'openid'

const templates: { template: Template; label: string; hint: string }[] = [
  { template: 'github-app', label: 'GitHub', hint: 'One click: PrikBoard creates a GitHub App for you' },
  { template: 'google', label: 'Google', hint: 'Google accounts and Workspace' },
  { template: 'discord', label: 'Discord', hint: 'Discord accounts' },
  { template: 'steam', label: 'Steam', hint: 'Steam accounts, no app to register' },
  { template: 'openid', label: 'Other (OpenID Connect)', hint: 'Microsoft Entra, Auth0, Okta, Keycloak, Authentik and others' },
  { template: 'github', label: 'GitHub OAuth App', hint: 'An app you register on GitHub yourself' },
]

/** How each form differs: where to register the app, what the provider calls the callback URL, which fields it needs. */
const forms: Record<
  Exclude<Template, 'github-app'>,
  { type: ProviderType; label: string; url: string; where: string; callback: string | null; clientId: boolean; secret: string; secretRequired: boolean }
> = {
  github: {
    type: 'GitHub',
    label: 'GitHub',
    url: 'https://github.com/settings/applications/new',
    where: 'GitHub → Settings → Developer settings → OAuth Apps',
    callback: 'Authorization callback URL',
    clientId: true,
    secret: 'Client secret',
    secretRequired: true,
  },
  google: {
    type: 'OpenIdConnect',
    label: 'Google',
    url: 'https://console.cloud.google.com/apis/credentials',
    where: 'Google Cloud console → Credentials → OAuth client ID (Web application)',
    callback: 'Authorized redirect URI',
    clientId: true,
    secret: 'Client secret',
    secretRequired: true,
  },
  discord: {
    type: 'Discord',
    label: 'Discord',
    url: 'https://discord.com/developers/applications',
    where: 'Discord Developer Portal → your application → OAuth2',
    callback: 'Redirect',
    clientId: true,
    secret: 'Client secret',
    secretRequired: true,
  },
  steam: {
    type: 'Steam',
    label: 'Steam',
    url: 'https://steamcommunity.com/dev/apikey',
    where: 'a Steam Web API key',
    callback: null,
    clientId: false,
    secret: 'Web API key (optional)',
    secretRequired: false,
  },
  openid: {
    type: 'OpenIdConnect',
    label: '',
    url: '',
    where: 'Your provider’s admin console: a confidential web client using the authorization code flow',
    callback: 'Redirect or callback URL',
    clientId: true,
    secret: 'Client secret',
    secretRequired: false,
  },
}

const callbackUrl = (id: string) => `${window.location.origin}/api/auth/callback/${id || '…'}`

/** The first of base, base-2, base-3 … that no provider has. */
function freeId(base: string, providers: SignInProviderDetail[]) {
  const taken = new Set(providers.map((p) => p.id))
  if (!taken.has(base)) return base
  let n = 2
  while (taken.has(`${base}-${n}`)) n++
  return `${base}-${n}`
}

/** Sends the browser to GitHub with the manifest, as the form post GitHub's manifest flow expects. */
function postGitHubManifest({ postUrl, manifest }: GitHubAppManifest) {
  const form = document.createElement('form')
  form.method = 'post'
  form.action = postUrl
  const field = document.createElement('input')
  field.type = 'hidden'
  field.name = 'manifest'
  field.value = manifest
  form.append(field)
  document.body.append(form)
  form.submit()
}

interface SignInProvidersProps {
  /** Where test sign-ins and a new GitHub App come back to: /#/setup or /#/admin. */
  returnUrl: string
  /** Called with the list whenever it loads. */
  onLoaded?: (providers: SignInProviderDetail[]) => void
}

/**
 * The sign-in providers, for the setup and the admin page: add GitHub (in one click, through a GitHub App), Google,
 * Discord, Steam or any OpenID Connect provider, test them, pick the default and remove them. Changes work at once.
 */
export function SignInProviders({ returnUrl, onLoaded }: SignInProvidersProps) {
  const [providers, setProviders] = useState<SignInProviderDetail[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState(takeSetupResult)
  const [adding, setAdding] = useState<Template | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [removing, setRemoving] = useState<SignInProviderDetail | null>(null)

  const reload = useCallback(
    () =>
      api.listSignInProviders().then(
        (list) => {
          setProviders(list)
          onLoaded?.(list)
        },
        (e) => setError(errorMessage(e)),
      ),
    [onLoaded],
  )

  useEffect(() => {
    void reload()
  }, [reload])

  async function change(action: () => Promise<unknown>) {
    setError(null)
    setResult(null)
    try {
      await action()
      return true
    } catch (e) {
      setError(errorMessage(e))
      return false
    } finally {
      await reload()
    }
  }

  return (
    <div className="setup-providers">
      {result && (
        <p className={result.ok ? 'setup-result' : 'sign-in-error'} role="status">
          {result.text}
        </p>
      )}
      {error && (
        <p className="sign-in-error" role="alert">
          {error}
        </p>
      )}

      {providers === null ? (
        <p className="status-inline">Loading…</p>
      ) : providers.length === 0 ? (
        <p className="dialog-hint">No sign-in providers yet. Add at least one, so people can sign in.</p>
      ) : (
        <ul className="row-list setup-provider-list">
          {providers.map((p) =>
            editing === p.id ? (
              <li key={p.id} className="setup-provider-editing">
                <EditProvider
                  provider={p}
                  onSave={async (changes) => {
                    if (await change(() => api.updateSignInProvider(p.id, changes))) setEditing(null)
                  }}
                  onCancel={() => setEditing(null)}
                />
              </li>
            ) : (
              <ProviderRow
                key={p.id}
                provider={p}
                returnUrl={returnUrl}
                onMakeDefault={() => void change(() => api.updateSignInProvider(p.id, { isDefault: true }))}
                onEdit={() => setEditing(p.id)}
                onRemove={() => setRemoving(p)}
              />
            ),
          )}
        </ul>
      )}

      {providers !== null &&
        (adding === null ? (
          <div className="setup-templates" role="group" aria-label="Add a sign-in provider">
            {templates.map((t) => (
              <button key={t.template} type="button" className="setup-template" onClick={() => setAdding(t.template)}>
                <span>{t.label}</span>
                <span className="dialog-hint">{t.hint}</span>
              </button>
            ))}
          </div>
        ) : adding === 'github-app' ? (
          <GitHubAppForm
            id={freeId('github', providers)}
            returnUrl={returnUrl}
            onCancel={() => setAdding(null)}
            onError={setError}
          />
        ) : (
          <AddProvider
            template={adding}
            id={freeId(adding === 'openid' ? 'sso' : adding, providers)}
            first={providers.length === 0}
            onAdd={async (provider) => {
              if (await change(() => api.createSignInProvider(provider))) setAdding(null)
            }}
            onCancel={() => setAdding(null)}
          />
        ))}

      {removing && (
        <ConfirmDialog
          title={`Remove ${removing.displayName}?`}
          message={
            removing.signIns > 0
              ? `${plural(removing.signIns, 'person signs', 'people sign')} in with it. Without another sign-in they can’t get in until a provider with the id “${removing.id}” is added again.`
              : 'Nobody signs in with it yet.'
          }
          confirmLabel="Remove"
          onConfirm={() => {
            const id = removing.id
            setRemoving(null)
            void change(() => api.deleteSignInProvider(id))
          }}
          onCancel={() => setRemoving(null)}
        />
      )}
    </div>
  )
}

interface ProviderRowProps {
  provider: SignInProviderDetail
  returnUrl: string
  onMakeDefault: () => void
  onEdit: () => void
  onRemove: () => void
}

function ProviderRow({ provider: p, returnUrl, onMakeDefault, onEdit, onRemove }: ProviderRowProps) {
  const fromSetup = p.source === 'Setup'
  return (
    <li className="setup-provider">
      <div className="setup-provider-main">
        <div className="row-main">
          <span>{p.displayName}</span>
          <span className="badge">{p.id}</span>
          {p.isDefault && <span className="badge setup-default">default</span>}
          {!fromSetup && <span className="badge" title="Set in the server’s configuration (Auth:Providers)">configuration</span>}
        </div>
        <span className="row-meta">
          {p.type === 'OpenIdConnect' ? p.authority : p.type} · {plural(p.signIns, 'sign-in', 'sign-ins')}
        </span>
      </div>
      <div className="setup-provider-actions">
        <a className="ghost small" href={testSignInUrl(p.id, returnUrl)} title="Go through the provider and back, without signing in">
          Test sign-in
        </a>
        {p.gitHubAppUrl && (
          <a className="icon-button" href={p.gitHubAppUrl} target="_blank" rel="noreferrer" title="The GitHub App" aria-label="The GitHub App">
            <ExternalIcon />
          </a>
        )}
        {fromSetup && !p.isDefault && (
          <button type="button" className="ghost small" onClick={onMakeDefault}>
            Make default
          </button>
        )}
        {fromSetup && (
          <>
            <button type="button" className="icon-button" onClick={onEdit} title="Change" aria-label={`Change ${p.displayName}`}>
              <PencilIcon />
            </button>
            <button type="button" className="icon-button" onClick={onRemove} title="Remove" aria-label={`Remove ${p.displayName}`}>
              <TrashIcon />
            </button>
          </>
        )}
      </div>
    </li>
  )
}

function CallbackUrl({ id, label }: { id: string; label: string }) {
  const copier = useCopy(2000)
  return (
    <label className="setup-field">
      <span>{label}</span>
      <CopyField label={label} value={callbackUrl(id)} copier={copier} />
    </label>
  )
}

interface AddProviderProps {
  template: Exclude<Template, 'github-app'>
  id: string
  /** The first provider becomes the default anyway. */
  first: boolean
  onAdd: (provider: CreateSignInProvider) => Promise<void>
  onCancel: () => void
}

/** A provider registered by hand: its callback URL to register, then the client id and secret it got. */
function AddProvider({ template, id: suggested, first, onAdd, onCancel }: AddProviderProps) {
  const form = forms[template]
  const [id, setId] = useState(suggested)
  const [displayName, setDisplayName] = useState(form.label)
  const [authority, setAuthority] = useState('')
  const [clientId, setClientId] = useState('')
  const [clientSecret, setClientSecret] = useState('')
  const [verified, setVerified] = useState(false)
  const [isDefault, setIsDefault] = useState(false)
  const [check, setCheck] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)

  const issuer = template === 'google' ? 'https://accounts.google.com' : template === 'openid' ? authority.trim() : undefined
  const valid =
    /^[a-z0-9][a-z0-9-]*$/.test(id) &&
    (!form.clientId || clientId.trim() !== '') &&
    (!form.secretRequired || clientSecret.trim() !== '') &&
    (template !== 'openid' || authority.trim() !== '')

  async function checkAuthority() {
    setCheck(null)
    try {
      const found = await api.checkOpenIdAuthority(authority.trim())
      setCheck({ ok: true, text: `Found ${found.issuer}.` })
    } catch (e) {
      setCheck({ ok: false, text: errorMessage(e) })
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!valid || busy) return
    setBusy(true)
    await onAdd({
      id,
      type: form.type,
      displayName: displayName.trim() || undefined,
      authority: issuer,
      clientId: form.clientId ? clientId.trim() : undefined,
      clientSecret: clientSecret.trim() || undefined,
      emailsAreVerified: template === 'openid' ? verified : undefined,
      isDefault: isDefault || undefined,
    })
    setBusy(false)
  }

  const where = form.url ? (
    <a href={form.url} target="_blank" rel="noreferrer">
      {form.where}
    </a>
  ) : (
    form.where
  )

  return (
    <form className="token-form setup-form" onSubmit={submit}>
      {form.callback ? (
        <>
          <p className="dialog-text">
            Register PrikBoard in {where}, with this {form.callback.toLowerCase()}:
          </p>
          <CallbackUrl id={id} label={form.callback} />
        </>
      ) : (
        <p className="dialog-text">Steam needs no app registration. With {where}, people get their Steam name and picture.</p>
      )}
      <div className="setup-row">
        <label className="setup-field">
          <span>Id</span>
          <input value={id} onChange={(e) => setId(e.target.value.toLowerCase())} aria-invalid={!/^[a-z0-9][a-z0-9-]*$/.test(id)} />
        </label>
        <label className="setup-field">
          <span>Button label</span>
          <input value={displayName} placeholder={id} onChange={(e) => setDisplayName(e.target.value)} maxLength={50} />
        </label>
      </div>
      {template === 'openid' && (
        <label className="setup-field">
          <span>Issuer URL</span>
          <div className="copy-field">
            <input
              value={authority}
              onChange={(e) => {
                setAuthority(e.target.value)
                setCheck(null)
              }}
              placeholder="https://sso.example.com/realms/main"
            />
            <button type="button" className="ghost small" disabled={!authority.trim()} onClick={() => void checkAuthority()}>
              Check
            </button>
          </div>
          {check && <span className={check.ok ? 'setup-ok' : 'widget-error'}>{check.text}</span>}
        </label>
      )}
      <div className="setup-row">
        {form.clientId && (
          <label className="setup-field">
            <span>Client id</span>
            <input value={clientId} onChange={(e) => setClientId(e.target.value)} autoComplete="off" />
          </label>
        )}
        <label className="setup-field">
          <span>{form.secret}</span>
          <input type="password" value={clientSecret} onChange={(e) => setClientSecret(e.target.value)} autoComplete="new-password" />
        </label>
      </div>
      {template === 'openid' && (
        <label className="check-row">
          <input type="checkbox" checked={verified} onChange={(e) => setVerified(e.target.checked)} />
          Trust its email addresses as verified (only for an organization you control)
        </label>
      )}
      {!first && (
        <label className="check-row">
          <input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} />
          Offer it first on the sign-in page
        </label>
      )}
      <div className="dialog-actions">
        <button type="button" className="ghost" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="primary" disabled={!valid || busy}>
          Add provider
        </button>
      </div>
    </form>
  )
}

interface GitHubAppFormProps {
  id: string
  returnUrl: string
  onCancel: () => void
  onError: (error: string) => void
}

/** Creating a GitHub App from a manifest: GitHub asks to confirm, then sends the browser back with the app's credentials. */
function GitHubAppForm({ id, returnUrl, onCancel, onError }: GitHubAppFormProps) {
  const [name, setName] = useState(() => `PrikBoard ${window.location.hostname}`.slice(0, 34))
  const [organization, setOrganization] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      postGitHubManifest(
        await api.beginGitHubApp({ id, name: name.trim(), organization: organization.trim() || undefined, returnUrl }),
      )
    } catch (error) {
      onError(errorMessage(error))
      setBusy(false)
    }
  }

  return (
    <form className="token-form setup-form" onSubmit={submit}>
      <p className="dialog-text">
        GitHub opens with a new app filled in; create it there and you come straight back. It can only read the email
        address of whoever signs in with it.
      </p>
      <div className="setup-row">
        <label className="setup-field">
          <span>App name (unique on GitHub)</span>
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={34} />
        </label>
        <label className="setup-field">
          <span>Organization (optional)</span>
          <input value={organization} onChange={(e) => setOrganization(e.target.value)} placeholder="Your own account" />
        </label>
      </div>
      <div className="dialog-actions">
        <button type="button" className="ghost" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="primary" disabled={!name.trim() || busy}>
          Continue on GitHub
        </button>
      </div>
    </form>
  )
}

interface EditProviderProps {
  provider: SignInProviderDetail
  onSave: (changes: { displayName?: string; authority?: string; clientId?: string; clientSecret?: string }) => Promise<void>
  onCancel: () => void
}

/** Changes a provider added in the setup; a blank secret keeps the one stored. */
function EditProvider({ provider, onSave, onCancel }: EditProviderProps) {
  const [displayName, setDisplayName] = useState(provider.displayName)
  const [authority, setAuthority] = useState(provider.authority ?? '')
  const [clientId, setClientId] = useState(provider.clientId)
  const [clientSecret, setClientSecret] = useState('')
  const oidc = provider.type === 'OpenIdConnect'
  const steam = provider.type === 'Steam'

  return (
    <form
      className="token-form setup-form"
      onSubmit={(e) => {
        e.preventDefault()
        void onSave({
          displayName,
          authority: oidc ? authority : undefined,
          clientId: clientId.trim() || undefined,
          clientSecret: clientSecret.trim() || undefined,
        })
      }}
    >
      {!steam && <CallbackUrl id={provider.id} label="Callback URL" />}
      <div className="setup-row">
        <label className="setup-field">
          <span>Button label</span>
          <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} maxLength={50} />
        </label>
        {!steam && (
          <label className="setup-field">
            <span>Client id</span>
            <input value={clientId} onChange={(e) => setClientId(e.target.value)} autoComplete="off" />
          </label>
        )}
      </div>
      {oidc && (
        <label className="setup-field">
          <span>Issuer URL</span>
          <input value={authority} onChange={(e) => setAuthority(e.target.value)} />
        </label>
      )}
      <label className="setup-field">
        <span>{steam ? 'New Web API key' : 'New client secret'}</span>
        <input
          type="password"
          value={clientSecret}
          onChange={(e) => setClientSecret(e.target.value)}
          placeholder={provider.hasSecret ? 'Leave empty to keep the stored one' : 'None stored'}
          autoComplete="new-password"
        />
      </label>
      <div className="dialog-actions">
        <button type="button" className="ghost" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="primary">
          Save
        </button>
      </div>
    </form>
  )
}
