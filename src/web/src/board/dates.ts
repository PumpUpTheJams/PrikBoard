/** A date as "21 Oct 2026", in the browser's language. */
export const shortDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

/** How long ago, as "just now", "5 minutes ago" or "yesterday"; a week or more back is a short date. */
export function sinceDate(iso: string, now = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000)
  if (seconds > -45) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes > -60) return relative.format(minutes, 'minute')
  const hours = Math.round(minutes / 60)
  if (hours > -24) return relative.format(hours, 'hour')
  const days = Math.round(hours / 24)
  if (days > -7) return relative.format(days, 'day')
  return shortDate(iso)
}
