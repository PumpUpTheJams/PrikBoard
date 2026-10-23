/** Agents of this issuer are Discord servers connected for link previews (docs/discord.md). */
export const DISCORD_ISSUER = 'discord'

/** The code from the Discord bot's "Connect your PrikBoard account" button (#/connect/discord-account?code=…), or null. */
export function discordAccountCodeFromHash(): string | null {
  const match = window.location.hash.match(/^#\/connect\/discord-account\?(.*)$/)
  if (!match) return null
  return new URLSearchParams(match[1]).get('code') ?? ''
}
