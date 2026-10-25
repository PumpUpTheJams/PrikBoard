export const ZOOM_LEVELS = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2]
export const MIN_ZOOM = ZOOM_LEVELS[0]
/** Below this zoom, cards on the board show only their fill, title and tag colors (FarCard in boardItemViews). */
export const FAR_ZOOM = 0.45
export const MAX_ZOOM = ZOOM_LEVELS[ZOOM_LEVELS.length - 1]

/** The next zoom level in a direction, or the current zoom at either end. */
export function stepZoom(zoom: number, direction: 1 | -1) {
  const next = direction > 0 ? ZOOM_LEVELS.find((z) => z > zoom + 1e-6) : ZOOM_LEVELS.findLast((z) => z < zoom - 1e-6)
  return next ?? zoom
}

/** How much the zoom changes per pixel of wheel travel: about 10% for one notch of a mouse wheel (100 px). */
const WHEEL_RATE = 0.001

/**
 * The zoom after `travel` pixels of wheel (negative zooms in). Smooth rather than in levels, at most about a quarter
 * per frame however fast the wheel spins, within the zoom limits; close to 100% it settles on 100%, so text is sharp.
 */
export function wheelZoom(zoom: number, travel: number) {
  const capped = Math.max(-300, Math.min(300, travel))
  const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom * Math.exp(-capped * WHEEL_RATE)))
  return Math.abs(next - 1) < 0.01 ? 1 : next
}
