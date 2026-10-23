import { useCallback } from 'react'
import { errorMessage } from '../api/client'

/**
 * Runs a change and then `reload`s, whether it worked or not; a refusal (such as "last owner") goes to `setError`, which
 * is cleared first. Resolves whether the change worked.
 */
export function useAttempt(reload: () => Promise<unknown>, setError: (error: string | null) => void) {
  return useCallback(
    async (action: () => Promise<unknown>) => {
      setError(null)
      try {
        await action()
        return true
      } catch (e) {
        setError(errorMessage(e))
        return false
      } finally {
        await reload()
      }
    },
    [reload, setError],
  )
}

/** What useAttempt returns, for parts that get it passed down. */
export type Attempt = ReturnType<typeof useAttempt>
