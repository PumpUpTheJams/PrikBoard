# Themes

PrikBoard's colors and fonts come from a **theme file**: a small JSON file of CSS custom properties. The built-in themes are in `src/web/src/themes/`: Productivity and Positivity, and the playful Sorbet, Bubblegum, Pool party, Chalkboard, Pop and Cloud, which extend one of those two. They are described by `theme.schema.json`, which an editor uses to complete and explain every field.

## A theme file

```json
{
  "$schema": "./theme.schema.json",
  "format": 1,
  "id": "sorbet",
  "name": "Sorbet",
  "description": "Cream paper, deep teal bars and a tomato accent.",
  "colorScheme": "light",
  "extends": "positivity",
  "fonts": ["Nunito:wght@400;500;600;700"],
  "tokens": {
    "font": "Nunito, ui-rounded, system-ui, sans-serif",
    "bg": "#fbf0e4",
    "accent": "#e24e2f",
    "fill-butter": "#f8c93e"
  },
  "bar": {
    "colorScheme": "dark",
    "tokens": { "text": "#fff4e6" }
  }
}
```

| Field | Meaning |
|---|---|
| `format` | The file format's version, `1`. |
| `id` | Lower case letters, digits and dashes. The browser remembers the chosen theme by it, so keep it once people use the theme. |
| `name`, `description` | Shown in the theme picker. |
| `colorScheme` | `dark` or `light`: the browser's own controls (fields, scroll bars) follow it. |
| `extends` | Optional: a built-in theme (`productivity`, `positivity`) whose tokens this one starts from, so the file lists only what it changes. Without it, the file lists every token. |
| `fonts` | Optional: up to three Google Fonts families the font tokens use, written as in a fonts.googleapis.com `css2` link (`Nunito:wght@400;600`). The app loads them from Google Fonts when the theme is picked, as it does Geist. A theme that extends another keeps its fonts unless it lists its own. |
| `tokens` | The tokens, named as in the CSS without the leading `--` (`bg` is `--bg`). The schema lists them all, with what each colors. |
| `bar` | Optional: tokens that change on the top bar and the phone layout's bars, for bars whose color differs from the page (Positivity's navy bars on a light page). Menus opened from a bar take the page's tokens again. A theme that extends another keeps that theme's bar tokens unless it changes them. |

## Values

Every value is checked before the theme is used:

- a **color** token takes a CSS color (`#fbf0e4`, `rgba(20, 23, 29, 0.8)`, `transparent`) or `var(--other-token)`;
- a **shadow** token takes a CSS `box-shadow`;
- a **font** token takes a `font-family` list: a family in `fonts`, one the page already loads (Geist, Geist Mono), or one the device has.

No `url(...)`, comments, `;`, `{`, `}`, `<`, `>`, `\`, `!` or `@`: a theme can't load anything but its `fonts` from Google Fonts, or reach outside its own values, so a theme someone else made is safe to try. A theme with a mistake isn't shown; the checker names each field that's wrong.

## Sizes stay put

A theme only changes colors, fonts and shadows. Every element keeps its size, so switching themes moves nothing on the board; with another font, only where a long title wraps inside its card can differ.

## In the code

- `themes/themeFile.ts`: `parseThemeFile` checks a file and fills in what it extends; `themeCss` turns a theme into the stylesheet the page uses.
- `theme.ts`: the built-in themes (`BUILT_IN_THEMES`, imported in the order the settings menu lists them), the one stored in this browser, and `applyTheme`, which loads the theme's fonts, puts the stylesheet on the page and fades from the old theme to the new one.
- `index.css` uses only the tokens. A new color the themes should be able to change becomes a token: add it to `theme.schema.json` and to `productivity.json` (Positivity extends it, so give it a value there too if the light theme needs a different one).
