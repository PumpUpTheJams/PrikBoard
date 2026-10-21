/**
 * Moving between the mobile screens. Every screen is an address (board, bucket, card, image; see board/links.ts), so a
 * reload or a shared link lands on it. Going forward adds a history entry, so the phone's back gesture goes back; the
 * back button in the top bar does the same when the screen was reached that way, and otherwise (a link from elsewhere,
 * a reload) goes up to the screen above, replacing the entry.
 */

/** Marks history entries made here, with how many screens deep they are. */
interface NavState {
  mobileDepth: number
}

const depth = () => (history.state as NavState | null)?.mobileDepth ?? 0

/** pushState and replaceState don't fire hashchange; App and the screens listen for it. */
const announce = () => window.dispatchEvent(new HashChangeEvent('hashchange'))

/** The hash of a path such as `/#/boards/abc123` (`#/boards/abc123`); an empty path is the boards list. */
const hashOf = (path: string) => (path.startsWith('/#') ? path.slice(1) : path || '#/')

/** Opens a screen: a new history entry, or with `replace` (switching buckets with the tabs) the same one. */
export function navigate(path: string, { replace = false }: { replace?: boolean } = {}) {
  const hash = hashOf(path)
  if (window.location.hash === hash) return
  if (replace) history.replaceState(history.state, '', hash)
  else history.pushState({ mobileDepth: depth() + 1 } satisfies NavState, '', hash)
  announce()
}

/** Goes back to the screen this one was opened from, or else up to `parent`. */
export function back(parent: string) {
  if (depth() > 0) {
    history.back()
    return
  }
  history.replaceState(null, '', hashOf(parent))
  announce()
}
