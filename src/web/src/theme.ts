import { useCallback, useState } from 'react'

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

/** Shows the page in a scheme (main.tsx does this before the first render, so it doesn't flash). */
export function applyColorScheme(scheme: ColorScheme) {
  if (scheme === 'positivity') document.documentElement.dataset.theme = 'positivity'
  else delete document.documentElement.dataset.theme
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
    applyColorScheme(next)
    setScheme(next)
  }, [])
  return { scheme, choose }
}
