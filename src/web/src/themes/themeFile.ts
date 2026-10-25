import schema from './theme.schema.json'

/**
 * Theme files (theme.schema.json, docs/themes.md): a theme is data, the CSS custom properties the app is drawn with,
 * checked here before any of it reaches the page, so a theme someone else made can't load anything or break out of
 * its values. `parseThemeFile` checks a file and fills in what it takes from the theme it extends; `themeCss` turns the
 * result into the one stylesheet theme.ts puts on the page.
 */

type Kind = 'color' | 'shadow' | 'font'
type Scheme = 'dark' | 'light'

/** A theme ready to show: every token has a value. */
export interface Theme {
  id: string
  name: string
  description: string
  colorScheme: Scheme
  /** Google Fonts families its font tokens use (css2 syntax), loaded when it is shown. */
  fonts: readonly string[]
  tokens: Readonly<Record<string, string>>
  /** Tokens that change on the bars, when the bars differ from the page. */
  bar?: { colorScheme: Scheme; tokens: Readonly<Record<string, string>> }
}

/** Every token a theme sets, with what kind of CSS value it takes, read from the schema. */
export const TOKEN_KINDS: ReadonlyMap<string, Kind> = new Map(
  Object.entries(schema.$defs.tokens.properties).map(([name, property]) => [name, property.$ref.slice('#/$defs/'.length) as Kind]),
)

/** The CSS property a value of each kind is checked against. */
const CHECKED_AS: Record<Kind, string> = { color: 'color', shadow: 'box-shadow', font: 'font-family' }

/** Anything that could load a resource, end the declaration or start another rule. */
const FORBIDDEN = /[;{}<>\\\n\r!@]|\/\*|url\s*\(|image(-set)?\s*\(|src\s*\(/i
const TOKEN_REF = /var\(\s*--([a-z0-9-]+)\s*\)/g
const ID = /^[a-z0-9][a-z0-9-]{0,39}$/
const FONT = new RegExp(schema.properties.fonts.items.pattern)
const FIELDS = new Set(['$schema', 'format', 'id', 'name', 'description', 'colorScheme', 'extends', 'fonts', 'tokens', 'bar'])
const isScheme = (value: unknown): value is Scheme => value === 'dark' || value === 'light'
const isObject = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)

/** Why a token's value can't be used, or null when it can. */
function valueProblem(kind: Kind, value: unknown): string | null {
  if (typeof value !== 'string' || !value.trim()) return 'must be a non-empty string'
  if (value.length > 200) return 'is longer than 200 characters'
  if (FORBIDDEN.test(value)) return 'may only be a plain CSS value (no url(), comments, ; { } < > \\ ! or @)'
  for (const [, name] of value.matchAll(TOKEN_REF)) if (!TOKEN_KINDS.has(name)) return `names --${name}, which is not a token`
  if (value.replace(TOKEN_REF, '').includes('var(')) return 'may only use var(--token), without a fallback'
  if (typeof CSS !== 'undefined' && !CSS.supports(CHECKED_AS[kind], value)) return `is not a valid ${kind}`
  return null
}

function checkTokens(tokens: unknown, where: string, errors: string[]): Record<string, string> {
  if (!isObject(tokens)) {
    errors.push(`${where} must be an object`)
    return {}
  }
  const checked: Record<string, string> = {}
  for (const [name, value] of Object.entries(tokens)) {
    const kind = TOKEN_KINDS.get(name)
    if (!kind) {
      errors.push(`${where}.${name} is not a token`)
      continue
    }
    const problem = valueProblem(kind, value)
    if (problem) errors.push(`${where}.${name} ${problem}`)
    else checked[name] = (value as string).trim()
  }
  return checked
}

/**
 * Checks a theme file and resolves it into a complete theme. `extends` may name one of `themes` (the built-in ones);
 * the file's tokens, and its bar's, go over that theme's.
 */
export function parseThemeFile(data: unknown, themes: ReadonlyMap<string, Theme>): { theme: Theme } | { errors: string[] } {
  if (!isObject(data)) return { errors: ['A theme file must be a JSON object'] }
  const errors: string[] = []
  for (const field of Object.keys(data)) if (!FIELDS.has(field)) errors.push(`${field} is not a theme field`)
  if (data.format !== 1) errors.push('format must be 1')
  if (typeof data.id !== 'string' || !ID.test(data.id)) errors.push('id must be lower case letters, digits and dashes, at most 40')
  if (typeof data.name !== 'string' || !data.name.trim() || data.name.length > 40) errors.push('name must be 1 to 40 characters')
  if (data.description !== undefined && (typeof data.description !== 'string' || data.description.length > 200))
    errors.push('description must be at most 200 characters')
  if (!isScheme(data.colorScheme)) errors.push('colorScheme must be "dark" or "light"')

  let base: Theme | undefined
  if (data.extends !== undefined) {
    base = typeof data.extends === 'string' ? themes.get(data.extends) : undefined
    if (!base) errors.push(`extends must name a built-in theme (${[...themes.keys()].join(', ')})`)
  }
  let fonts = base?.fonts ?? []
  if (data.fonts !== undefined) {
    if (!Array.isArray(data.fonts) || data.fonts.length > 3 || !data.fonts.every((font) => typeof font === 'string' && FONT.test(font)))
      errors.push('fonts must be at most 3 Google Fonts families, such as "Nunito:wght@400;600"')
    else fonts = data.fonts
  }
  const tokens = { ...base?.tokens, ...checkTokens(data.tokens, 'tokens', errors) }
  const missing = [...TOKEN_KINDS.keys()].filter((name) => !(name in tokens))
  if (missing.length) errors.push(`tokens is missing ${missing.join(', ')} (or extend a built-in theme)`)

  let barScheme = base?.bar?.colorScheme
  let barTokens: Record<string, string> = { ...base?.bar?.tokens }
  if (data.bar !== undefined) {
    if (!isObject(data.bar)) errors.push('bar must be an object')
    else {
      for (const field of Object.keys(data.bar)) if (field !== 'colorScheme' && field !== 'tokens') errors.push(`bar.${field} is not a bar field`)
      if (data.bar.colorScheme !== undefined && !isScheme(data.bar.colorScheme)) errors.push('bar.colorScheme must be "dark" or "light"')
      else if (isScheme(data.bar.colorScheme)) barScheme = data.bar.colorScheme
      if (data.bar.tokens !== undefined) barTokens = { ...barTokens, ...checkTokens(data.bar.tokens, 'bar.tokens', errors) }
    }
  }

  if (errors.length) return { errors }
  const colorScheme = data.colorScheme as Scheme
  return {
    theme: {
      id: data.id as string,
      name: (data.name as string).trim(),
      description: (data.description as string | undefined) ?? '',
      colorScheme,
      fonts,
      tokens,
      bar: Object.keys(barTokens).length ? { colorScheme: barScheme ?? colorScheme, tokens: barTokens } : undefined,
    },
  }
}

/**
 * A token's value in a theme with its var(--token) references filled in from the same theme, for drawing a theme
 * somewhere other than the page (the theme picker's previews).
 */
export function tokenValue(theme: Theme, name: string, depth = 0): string {
  const value = theme.tokens[name] ?? ''
  return depth > 4 ? value : value.replace(TOKEN_REF, (_, ref: string) => tokenValue(theme, ref, depth + 1))
}

/** The Google Fonts stylesheet that loads a theme's fonts, or null when it needs none. */
export function fontsUrl(theme: Theme): string | null {
  if (!theme.fonts.length) return null
  return `https://fonts.googleapis.com/css2?${theme.fonts.map((font) => `family=${font.replaceAll(' ', '+')}`).join('&')}&display=swap`
}

/** The bars that carry a theme's bar tokens (the top bar and the phone layout's bars). */
const BARS = ':is(.topbar, .m-bar, .m-tabs-bar, .m-actions, .m-search.in-bar)'

const declarations = (tokens: Readonly<Record<string, string>>) =>
  Object.entries(tokens)
    .map(([name, value]) => `  --${name}: ${value};`)
    .join('\n')

/**
 * The stylesheet that shows a theme: its tokens on the page and, when its bars differ, theirs on the bars, with menus
 * opened from a bar taking the page's tokens again. Inherited text resolves its color where it is set, so the bars and
 * their menus set it again from their own tokens.
 */
export function themeCss(theme: Theme): string {
  let css = `:root {\n${declarations(theme.tokens)}\n  color-scheme: ${theme.colorScheme};\n}\n`
  if (theme.bar) {
    css += `:root ${BARS} {\n${declarations(theme.bar.tokens)}\n  color-scheme: ${theme.bar.colorScheme};\n  color: var(--text);\n}\n`
    css += `:root ${BARS} .menu-panel {\n${declarations(theme.tokens)}\n  color-scheme: ${theme.colorScheme};\n  color: var(--text);\n}\n`
  }
  return css
}
