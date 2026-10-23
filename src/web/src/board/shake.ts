/** How far the pointer must swing back (screen pixels) before a change of direction counts towards a shake. */
const SWING = 30
/** Changes of direction that make a shake... */
const FLIPS = 3
/** ...within this long (ms). */
const WINDOW_MS = 800

interface Axis {
  /** The way the pointer is going along this axis: 1, -1, or 0 before it has gone either way. */
  dir: number
  /** The furthest point reached in that direction. */
  extreme: number
  /** When it recently changed direction (ms). */
  flips: number[]
}

/**
 * Tells a shake from a drag: the pointer going back and forth quickly, left and right or up and down. Feed it every
 * pointer position of one drag; it answers true once the drag has become a shake.
 */
export function shakeDetector() {
  const axes: Axis[] = []
  return (x: number, y: number, at: number): boolean => {
    return [x, y].some((value, i) => {
      const axis = (axes[i] ??= { dir: 0, extreme: value, flips: [] })
      const travel = value - axis.extreme
      if (axis.dir === 0 || Math.sign(travel) === axis.dir) {
        // Still going the same way (or not yet either way): the furthest point moves along.
        if (Math.abs(travel) >= (axis.dir === 0 ? SWING : 0)) {
          axis.dir = Math.sign(travel) || axis.dir
          axis.extreme = value
        }
        return false
      }
      if (Math.abs(travel) < SWING) return false
      // Swung back far enough: a change of direction.
      axis.dir = -axis.dir
      axis.extreme = value
      axis.flips = [...axis.flips.filter((t) => at - t < WINDOW_MS), at]
      return axis.flips.length >= FLIPS
    })
  }
}
