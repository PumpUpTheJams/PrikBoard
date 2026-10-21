/** Colors that tell people apart on the board (drag outlines, presence rings); readable on both themes. */
const colors = ['#f2994a', '#56ccf2', '#6fcf97', '#bb6bd9', '#eb5757', '#f2c94c', '#2d9cdb', '#9b51e0']

/** A stable color per person or agent, so everyone sees the same person in the same color. */
export function presenceColor(principalId: string): string {
  let hash = 0
  for (const c of principalId) hash = (hash * 31 + c.charCodeAt(0)) | 0
  return colors[Math.abs(hash) % colors.length]
}
