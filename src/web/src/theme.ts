import { useCallback, useState } from 'react'
import { reducedMotion } from './board/motion'

/**
 * The app's color scheme: Productivity, the dark one, or Positivity, a light daytime one with the same card fills
 * (index.css, `data-theme` on the page). Picked in the settings menu, per browser.
 */
export type ColorScheme = 'productivity' | 'positivity'

const KEY = 'prikboard.colorScheme'

/** The scheme picked in this browser; Productivity until another is. */
export function storedColorScheme(): ColorScheme {
  try {
    return localStorage.getItem(KEY) === 'positivity' ? 'positivity' : 'productivity'
  } catch {
    return 'productivity'
  }
}

/**
 * Shows the page in a scheme (main.tsx does this before the first render, so it doesn't flash). With `fade`, the page
 * fades from the old scheme to the new one like a dimmer (a view transition, `.scheme-fade` in index.css); without
 * view transitions, or for reduced motion, it switches at once.
 */
export function applyColorScheme(scheme: ColorScheme, { fade = false } = {}) {
  const root = document.documentElement
  const apply = () => {
    if (scheme === 'positivity') root.dataset.theme = 'positivity'
    else delete root.dataset.theme
  }
  if (!fade || typeof document.startViewTransition !== 'function' || reducedMotion()) return apply()
  root.classList.add('scheme-fade')
  void document
    .startViewTransition(apply)
    .finished.catch(() => {})
    .finally(() => root.classList.remove('scheme-fade'))
}

/** The scheme in use, and `choose` to switch it here and remember it in this browser. */
export function useColorScheme() {
  const [scheme, setScheme] = useState(storedColorScheme)
  const choose = useCallback((next: ColorScheme) => {
    try {
      localStorage.setItem(KEY, next)
    } catch {
      // Without storage the choice lasts until the page reloads.
    }
    applyColorScheme(next, { fade: true })
    setScheme(next)
  }, [])
  return { scheme, choose }
}
