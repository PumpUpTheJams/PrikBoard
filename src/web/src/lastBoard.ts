/**
 * The board last open on this device, per signed-in user, so opening PrikBoard without a board in the address goes
 * back to it. Kept per user so someone else signing in on the same browser doesn't land on it.
 */
const keyOf = (userId: string) => `prikboard.lastBoard.${userId}`

/** The short id of the board `userId` last had open here, if any. */
export function rememberedBoard(userId: string): string | null {
  try {
    return localStorage.getItem(keyOf(userId))
  } catch {
    return null
  }
}

export function rememberBoard(userId: string, shortId: string) {
  try {
    localStorage.setItem(keyOf(userId), shortId)
  } catch {
    // Without storage the newest board opens instead.
  }
}
