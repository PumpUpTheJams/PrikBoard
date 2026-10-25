import { useCallback, useState } from 'react'
import { reducedMotion } from './board/motion'
import positivityFile from './themes/positivity.json'
import productivityFile from './themes/productivity.json'
import bubblegumFile from './themes/bubblegum.json'
import chalkboardFile from './themes/chalkboard.json'
import cloudFile from './themes/cloud.json'
import poolPartyFile from './themes/pool-party.json'
import popFile from './themes/pop.json'
import sorbetFile from './themes/sorbet.json'
import { fontsUrl, parseThemeFile, themeCss, type Theme } from './themes/themeFile'

/**
 * The app's theme: Productivity, the dark one, Positivity, a light daytime one with the same card fills, one of the
 * playful ones, or any other theme file (themes/*.json, docs/themes.md). Picked in the settings menu, per browser.
 */

/** The built-in themes, in the order the settings menu offers them; each may extend the ones before it. */
export const BUILT_IN_THEMES: readonly Theme[] = (() => {
  const themes = new Map<string, Theme>()
  for (const file of [productivityFile, positivityFile, sorbetFile, bubblegumFile, poolPartyFile, chalkboardFile, popFile, cloudFile]) {
    const parsed = parseThemeFile(file, themes)
    if ('errors' in parsed) throw new Error(`Built-in theme ${file.id}: ${parsed.errors.join('; ')}`)
    themes.set(parsed.theme.id, parsed.theme)
  }
  return [...themes.values()]
})()

const DEFAULT_THEME = BUILT_IN_THEMES[0]
const KEY = 'prikboard.colorScheme'

const themeById = (id: string | null) => BUILT_IN_THEMES.find((theme) => theme.id === id) ?? DEFAULT_THEME

/** The theme picked in this browser; Productivity until another is. */
export function storedTheme(): Theme {
  try {
    return themeById(localStorage.getItem(KEY))
  } catch {
    return DEFAULT_THEME
  }
}

/**
 * Shows the page in a theme, as one stylesheet of its tokens (main.tsx does this before the first render, so it
 * doesn't flash). With `fade`, the page fades from the old theme to the new one like a dimmer (a view transition,
 * `.scheme-fade` in index.css); without view transitions, or for reduced motion, it switches at once.
 */
export function applyTheme(theme: Theme, { fade = false } = {}) {
  const root = document.documentElement
  const sheet = document.getElementById('prikboard-theme') ?? document.head.appendChild(Object.assign(document.createElement('style'), { id: 'prikboard-theme' }))
  const css = themeCss(theme)
  // The theme's own fonts load first, so they are on their way before the page fades over.
  const fonts = fontsUrl(theme)
  const link = document.getElementById('prikboard-theme-fonts') as HTMLLinkElement | null
  if (fonts && link) link.href = fonts
  else if (fonts) document.head.append(Object.assign(document.createElement('link'), { id: 'prikboard-theme-fonts', rel: 'stylesheet', href: fonts }))
  else link?.remove()
  const apply = () => {
    sheet.textContent = css
  }
  if (!fade || typeof document.startViewTransition !== 'function' || reducedMotion()) return apply()
  root.classList.add('scheme-fade')
  void document
    .startViewTransition(apply)
    .finished.catch(() => {})
    .finally(() => root.classList.remove('scheme-fade'))
}

/** The theme in use, and `choose` to switch to another by id here and remember it in this browser. */
export function useTheme() {
  const [theme, setTheme] = useState(storedTheme)
  const choose = useCallback((id: string) => {
    const next = themeById(id)
    try {
      localStorage.setItem(KEY, next.id)
    } catch {
      // Without storage the choice lasts until the page reloads.
    }
    applyTheme(next, { fade: true })
    setTheme(next)
  }, [])
  return { theme, choose }
}
