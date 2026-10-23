/** What came back from a round trip the setup started: a test sign-in or creating a GitHub App. */
export interface SetupResult {
  ok: boolean
  text: string
}

const params = ['setupTest', 'setupGitHub', 'setupProvider', 'setupReason']

/** Whether the address holds a result, without taking it (the admin page opens its Instance tab for one). */
export const hasSetupResult = () => {
  const search = new URL(window.location.href).searchParams
  return search.has('setupTest') || search.has('setupGitHub')
}

/**
 * Reads and removes ?setupTest=… or ?setupGitHub=…, which the API adds when it sends a test sign-in or a new GitHub App
 * back to the page that started it.
 */
export function takeSetupResult(): SetupResult | null {
  const url = new URL(window.location.href)
  const search = url.searchParams
  const test = search.get('setupTest')
  const gitHub = search.get('setupGitHub')
  if (test === null && gitHub === null) return null
  const provider = search.get('setupProvider') ?? 'the provider'
  const reason = search.get('setupReason')
  for (const param of params) search.delete(param)
  window.history.replaceState(null, '', url)

  if (test !== null) {
    return test === 'ok'
      ? { ok: true, text: `Signing in with ${provider} works.` }
      : { ok: false, text: `Signing in with ${provider} didn’t work${reason ? `: ${reason}` : '.'}` }
  }
  return gitHub === 'ok'
    ? { ok: true, text: `The GitHub App is ready: people can sign in with GitHub (${provider}).` }
    : { ok: false, text: `The GitHub App wasn’t added${reason ? `: ${reason}` : '.'}` }
}
