import type { BoardTag, TagColor } from '../api/types'

/** Each tag's colour by name, for drawing tags wherever they show. */
export type TagColors = ReadonlyMap<string, TagColor>

/** A colour shown on one tag before it is picked: the one under the pointer in the palette. */
export interface TagColorPreview {
  tagId: string
  color: TagColor
}

/**
 * The board's tag colours, with shown colours in place of saved ones: a colour being saved (until the board reloads
 * with it) and the one under the pointer, the later ones winning.
 */
export function tagColorsOf(tags: BoardTag[], ...shown: (TagColorPreview | null)[]): TagColors {
  const colors = new Map(tags.map((t) => [t.name, t.color]))
  for (const s of shown) {
    const tag = s && tags.find((t) => t.id === s.tagId)
    if (s && tag) colors.set(tag.name, s.color)
  }
  return colors
}

/** Class names for a chip of this tag: none for Default (a shade of what it sits on), else a solid chip of its colour. */
export function tagColorClass(colors: TagColors | undefined, name: string) {
  const color = colors?.get(name)
  return color && color !== 'Default' ? ` tag-colored tag-color-${color.toLowerCase()}` : ''
}
