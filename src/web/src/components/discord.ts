/** Agents of this issuer are Discord servers connected for link previews (docs/discord.md). */
export const DISCORD_ISSUER = 'discord'

/** What the PrikBoard Discord bot put in the link it posted: #/connect/discord?code_challenge=… */
export interface DiscordConnectRequest {
  codeChallenge: string
}

/** The bot's connect request from the hash, or null when this is not one. */
export function discordConnectFromHash(): DiscordConnectRequest | null {
  const match = window.location.hash.match(/^#\/connect\/discord(?:\?(.*))?$/)
  if (!match) return null
  return { codeChallenge: new URLSearchParams(match[1] ?? '').get('code_challenge') ?? '' }
}
