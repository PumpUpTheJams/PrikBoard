/** Agents of this issuer are Discord servers connected for link previews (docs/discord.md). */
export const DISCORD_ISSUER = 'discord'

/** Agents of this issuer are Slack workspaces connected for link previews (docs/slack.md). */
export const SLACK_ISSUER = 'slack'

/** The chat apps whose bots link a chat account to a PrikBoard user, for Save as card. */
export type ChatPlatform = 'Discord' | 'Slack'

/**
 * The code from a bot's "Connect your PrikBoard account" link (#/connect/discord-account?code=… or
 * #/connect/slack-account?code=…), with the chat app it is for, or null.
 */
export function chatAccountLinkFromHash(): { platform: ChatPlatform; code: string } | null {
  const match = window.location.hash.match(/^#\/connect\/(discord|slack)-account\?(.*)$/)
  if (!match) return null
  return { platform: match[1] === 'slack' ? 'Slack' : 'Discord', code: new URLSearchParams(match[2]).get('code') ?? '' }
}
