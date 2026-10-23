import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { CheckStatus, SystemCheck } from '../api/types'
import { saveFile } from '../board/files'
import { DownloadIcon } from '../board/icons'

const statusLabel: Record<CheckStatus, string> = { Ok: 'OK', Warning: 'Warning', Failed: 'Failed' }

/** How the server runs (getSystemChecks). */
export function SystemChecks() {
  const [checks, setChecks] = useState<SystemCheck[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const run = useCallback(
    () =>
      api.getSystemChecks().then(
        (found) => {
          setChecks(found)
          setError(null)
        },
        (e) => setError(errorMessage(e)),
      ),
    [],
  )

  useEffect(() => {
    void run()
  }, [run])

  return (
    <div className="setup-checks">
      {error && (
        <p className="sign-in-error" role="alert">
          {error}
        </p>
      )}
      {checks === null ? (
        !error && <p className="status-inline">Checking…</p>
      ) : (
        <ul className="setup-check-list">
          {checks.map((c) => (
            <li key={c.id} className={`setup-check ${c.status.toLowerCase()}`}>
              <span className="setup-check-status">{statusLabel[c.status]}</span>
              <div>
                <strong>{c.title}</strong>
                <p>{c.message}</p>
              </div>
            </li>
          ))}
        </ul>
      )}
      <div className="dialog-actions">
        <button type="button" className="ghost small" onClick={() => void run()}>
          Check again
        </button>
      </div>
    </div>
  )
}

/** Downloads the recovery kit (the hash key and how to restore a backup). Admins only. */
export function RecoveryKitButton({ onSaved }: { onSaved?: () => void }) {
  const [error, setError] = useState<string | null>(null)

  async function download() {
    setError(null)
    try {
      const { blob, fileName } = await api.getRecoveryKit()
      saveFile(blob, fileName)
      onSaved?.()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <>
      <button type="button" className="primary setup-download" onClick={() => void download()}>
        <DownloadIcon /> Download the recovery kit
      </button>
      {error && (
        <p className="sign-in-error" role="alert">
          {error}
        </p>
      )}
    </>
  )
}
