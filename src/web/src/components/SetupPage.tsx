import { useCallback, useState, type FormEvent, type ReactNode } from 'react'
import { api, errorMessage, loginUrl } from '../api/client'
import type { Caller, SetupStatus, SignInProviderDetail } from '../api/types'
import { CardIcon } from '../board/icons'
import { AccessTokens } from './AccessTokens'
import { SignInProviders } from './SignInProviders'
import { RecoveryKitButton, SystemChecks } from './SystemChecks'

type Step = 'key' | 'providers' | 'admin' | 'checks' | 'kit' | 'agent'

const steps: { step: Step; label: string }[] = [
  { step: 'key', label: 'Setup key' },
  { step: 'providers', label: 'Sign-in' },
  { step: 'admin', label: 'Admin' },
  { step: 'checks', label: 'Checks' },
  { step: 'kit', label: 'Recovery kit' },
  { step: 'agent', label: 'Agents' },
]

const isLocal = (host: string) => host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host.endsWith('.localhost')

interface SetupPageProps {
  /** Null until the first admin signed in; then the steps after signing in. */
  user: Caller | null
  /** For a visitor who is signed out. */
  status: SetupStatus | null
  onUnlocked: () => void
  onDone: () => void
}

/**
 * The first-run setup at #/setup, shown instead of the sign-in page while nobody has an account: enter the setup key
 * from the server's log, add sign-in providers, sign in as the first admin, then run the checks, keep the recovery kit
 * and connect an agent. In recovery mode (Setup__Recovery) the key opens the providers again.
 */
export function SetupPage({ user, status, onUnlocked, onDone }: SetupPageProps) {
  const [step, setStep] = useState<Step>(user ? 'checks' : status?.unlocked ? 'providers' : 'key')
  const [providers, setProviders] = useState<SignInProviderDetail[]>([])
  const recovery = status?.recovery ?? false
  const onLoaded = useCallback((list: SignInProviderDetail[]) => setProviders(list), [])

  if (user && !user.isAdmin) {
    return (
      <SetupFrame step={null}>
        <h1>PrikBoard is set up</h1>
        <p>Only admins can change how it runs.</p>
        <div className="dialog-actions">
          <button type="button" className="primary" onClick={onDone}>
            Go to your boards
          </button>
        </div>
      </SetupFrame>
    )
  }

  const http = window.location.protocol === 'http:' && !isLocal(window.location.hostname)

  return (
    <SetupFrame step={step}>
      {step === 'key' && (
        <>
          <h1>{recovery ? 'Recover PrikBoard' : 'Set up PrikBoard'}</h1>
          <p>
            {recovery
              ? 'PrikBoard runs in recovery mode. Enter the setup key from its log to change the sign-in providers.'
              : 'Welcome! Enter the setup key that PrikBoard printed in its log, to show that this server is yours.'}
          </p>
          <UnlockForm
            onUnlocked={() => {
              onUnlocked()
              setStep('providers')
            }}
          />
          <p className="dialog-hint">
            With Docker Compose: <code>docker compose logs app</code>. A restart prints a new key.
          </p>
        </>
      )}

      {step === 'providers' && (
        <>
          <h1>How do people sign in?</h1>
          <p>
            PrikBoard has no passwords: people sign in with an account they already have. Add one or more providers. Their
            callback URLs use this address, <code>{window.location.origin}</code>
            {window.location.hostname === 'localhost' ? ', which only works on this computer: add the providers again once PrikBoard has its own address.' : '.'}
          </p>
          {http && (
            <p className="sign-in-error" role="alert">
              This address is plain http. Sign-in providers need HTTPS on anything but localhost: put PrikBoard behind a proxy with
              a certificate first (Caddy in the compose file).
            </p>
          )}
          <SignInProviders returnUrl="/#/setup" onLoaded={onLoaded} />
          <div className="dialog-actions">
            <button type="button" className="primary" disabled={providers.length === 0} onClick={() => setStep('admin')}>
              Next
            </button>
          </div>
        </>
      )}

      {step === 'admin' && (
        <>
          <h1>{recovery ? 'Sign in' : 'Become the admin'}</h1>
          <p>
            {recovery
              ? 'Sign in with your account to check that it works again.'
              : 'Sign in now: the first person to sign in here becomes the admin of this PrikBoard. Everyone after you joins through an invite link.'}
          </p>
          <div className="sign-in-providers">
            {providers.map((p, i) => (
              <a key={p.id} className={i === 0 ? 'button primary' : 'button'} href={loginUrl(p.id, '/#/setup')}>
                Continue with {p.displayName}
              </a>
            ))}
          </div>
          <div className="dialog-actions">
            <button type="button" className="ghost small" onClick={() => setStep('providers')}>
              Back to the providers
            </button>
          </div>
        </>
      )}

      {step === 'checks' && (
        <>
          <h1>{user ? 'You’re the admin now!' : 'Checks'}</h1>
          <p>Here is how PrikBoard runs. Fix anything that failed; warnings can wait.</p>
          <SystemChecks />
          <div className="dialog-actions">
            <button type="button" className="primary" onClick={() => setStep('kit')}>
              Next
            </button>
          </div>
        </>
      )}

      {step === 'kit' && (
        <>
          <h1>Keep the recovery kit</h1>
          <p>
            PrikBoard stores no names or emails, only hashes made with a secret key. Without that key nobody can sign in to their
            account again, even from a backup. Download it and keep it somewhere safe and offline, such as a password manager.
          </p>
          <RecoveryKitButton />
          <p className="dialog-hint">Back up the database and the data volume (/data) too. You can download the kit again from Admin → Instance.</p>
          <div className="dialog-actions">
            <button type="button" className="primary" onClick={() => setStep('agent')}>
              Next
            </button>
          </div>
        </>
      )}

      {step === 'agent' && (
        <>
          <h1>Connect an agent</h1>
          <p>Optional: let Claude Code or another agent work on your boards over MCP. You can do this later in Account.</p>
          <AccessTokens boards={[]} />
          <div className="dialog-actions">
            <button type="button" className="primary" onClick={onDone}>
              Go to your boards
            </button>
          </div>
        </>
      )}
    </SetupFrame>
  )
}

function SetupFrame({ step, children }: { step: Step | null; children: ReactNode }) {
  const at = steps.findIndex((s) => s.step === step)
  return (
    <div className="sign-in">
      <div className="sign-in-card setup-card">
        <span className="board-tile large" aria-hidden="true">
          <CardIcon size={28} />
        </span>
        {step && (
          <ol className="setup-steps" aria-label="Setup steps">
            {steps.map((s, i) => (
              <li key={s.step} className={i < at ? 'done' : i === at ? 'current' : undefined} aria-current={i === at ? 'step' : undefined}>
                {s.label}
              </li>
            ))}
          </ol>
        )}
        {children}
      </div>
    </div>
  )
}

function UnlockForm({ onUnlocked }: { onUnlocked: () => void }) {
  const [key, setKey] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.unlockSetup(key.trim())
      onUnlocked()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="setup-unlock" onSubmit={submit}>
      <input
        value={key}
        onChange={(e) => setKey(e.target.value)}
        placeholder="XXXXX-XXXXX-XXXXX-XXXXX"
        aria-label="Setup key"
        aria-invalid={error !== null}
        autoComplete="off"
        spellCheck={false}
        autoFocus
      />
      <button type="submit" className="primary" disabled={!key.trim() || busy}>
        Unlock
      </button>
      {error && (
        <p className="sign-in-error" role="alert">
          {error}
        </p>
      )}
    </form>
  )
}
