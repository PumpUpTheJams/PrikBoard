export const ZOOM_LEVELS = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2]

/** The next zoom level in a direction, or the current zoom at either end. */
export function stepZoom(zoom: number, direction: 1 | -1) {
  const next = direction > 0 ? ZOOM_LEVELS.find((z) => z > zoom + 1e-6) : ZOOM_LEVELS.findLast((z) => z < zoom - 1e-6)
  return next ?? zoom
}
