const authErrors: Record<string, string> = {
  failed: 'Signing in did not work. Please try again.',
  invite_required: 'PrikBoard is invite-only. Open an invite link from a board owner to join.',
  identity_in_use: 'That sign-in already belongs to another PrikBoard account, so it was not connected.',
  link_failed: 'Connecting that sign-in did not work. Sign in again and retry from your account.',
}

/**
 * Reads and removes ?authError=… which the API adds when it sends a failed sign-in back here. A failed link of another
 * sign-in also comes back this way, while the person is still signed in.
 */
export function takeAuthError(): string | null {
  const url = new URL(window.location.href)
  const code = url.searchParams.get('authError')
  if (code === null) return null
  url.searchParams.delete('authError')
  window.history.replaceState(null, '', url)
  return authErrors[code] ?? authErrors.failed
}
