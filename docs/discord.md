# Design: Discord link previews

Status: built. Part 1 (PRIK-142) is the meta tags in `src/web/index.html` and the PNGs in `src/web/public` (sources in `src/web/icons`). Part 2 runs inside the API (PRIK-149, PRIK-179). The pieces are:

- `Integrations/` (the bot: `DiscordGateway`, `DiscordBot`, `LinkPreviews`)
- `Endpoints/DiscordEndpoints.cs` and `Endpoints/DiscordBotEndpoints.cs`
- In the web app, `components/IntegrationsDialog.tsx` (board menu → *Integrations*) and `components/DiscordBotSettings.tsx` (Admin → *Instance*)

It has not been tried against Discord yet, because that needs a public host (open question 10).

Part 2 started as a separate bot process that each server connected with a code pasted back into Discord. That was hard to set up: an env var on the API, a second container, and four steps per server. It was replaced by a bot inside the API that an admin sets up at runtime, plus a channel picker for board owners. The `DiscordBotInApi` migration deleted the old per-server tokens. Servers that were already connected keep their boards and channels, and their previews come back once an admin enters the same bot's token.

## Goal

When someone pastes a PrikBoard link in Discord, the chat should show what it points to, as far as it is allowed to. There are three cases:

| Where | Link | Preview |
| --- | --- | --- |
| Any chat app, or a Discord server without the integration | Any PrikBoard link | A small card: the PrikBoard icon with a *not signed in* badge. It says this is a PrikBoard link and that neither you nor the integration is signed in here. Nothing from the board. |
| A Discord server with the integration, for a board connected to it | Board or card | The board's name and description. For a card, its readable id, title and description, when there is one. |
| A Discord server with the integration | Invite (from *Share*) | *You're invited to join &lt;board name&gt;*, with the role and expiry. |

![Mockup: the anonymous preview, and the integration's card, board and invite embeds](discord-previews.svg)

### Constraints

- **Links stay as they are.** Board and card links keep everything behind the `#` (`/#/boards/{shortId}/cards/{number}`), and so do invites (`/#/invite/{token}`). There are no new share links or server-rendered pages.
- **Set up while PrikBoard runs.** No redeploy, no second container and no environment variable is needed. An admin pastes a bot token, and board owners pick channels.

## Why the chat app's own preview sees so little

Chat apps build link previews by fetching the URL themselves, anonymously. Browsers never send the part after `#` to the server, so every PrikBoard link reaches the server as `/`, and the crawler isn't signed in either. The chat app's own preview can therefore only ever be one fixed preview for all PrikBoard links, built from `index.html`.

The integration gets around both limits. The bot reads the link from the message text, fragment included, and looks it up as the Discord server it was pasted in.

## Part 1: the anonymous preview

### Meta tags in `index.html`

`src/web/index.html` has static meta tags. They are the same for every link, since every link is `/` to a crawler:

```html
<title>PrikBoard</title>
<meta name="description" content="Neither you nor a PrikBoard integration is signed in here. Open the link to sign in and see it.">
<meta property="og:site_name" content="PrikBoard">
<meta property="og:type" content="website">
<meta property="og:title" content="PrikBoard link">
<meta property="og:description" content="Neither you nor a PrikBoard integration is signed in here. Open the link to sign in and see it.">
<meta property="og:image" content="/og-signed-out.png">
<meta property="og:image:width" content="256">
<meta property="og:image:height" content="256">
<meta property="og:image:alt" content="The PrikBoard card with a not-signed-in badge">
<meta name="twitter:card" content="summary">
<meta name="theme-color" content="#ffd54f">
```

- **Small card.** `twitter:card` `summary` with a square image gives the small thumbnail on the right of the card, not a large banner.
- **No `og:url`.** It would tell the chat app that every link is the home page. Without it, the card links to what was pasted.
- **The image** (`src/web/public/og-signed-out.png`, 256 × 256) is the card logo on the Night shift background, with a small round badge in a corner. It is a PNG because many crawlers don't render SVG. `icon-512.png` and `apple-touch-icon.png` sit next to it, and the bot uses the icon.
- **Relative image URL.** Discord and most crawlers resolve `og:image` against the page URL. If one doesn't, the build can write the absolute URL from a `PRIKBOARD_PUBLIC_URL` build variable. Decide this when there is a host.
- **`theme-color`** `#ffd54f` (the logo's yellow) colors the bar beside Discord's card.

The text is the same wherever it appears: the home page, a board, a card, an invite. It never says which kind of link it is, because the server can't tell.

## Part 2: the Discord integration

### Setting up the bot (admins)

The instance has one Discord bot, and it runs inside the API (`Integrations/DiscordGateway.cs`). It connects out to Discord's gateway, so it needs no inbound port. To set it up, an admin goes to Admin → *Instance* → *Discord bot*:

1. Create an application at [discord.com/developers](https://discord.com/developers/applications).
2. On its *Bot* tab, turn on **Message Content Intent**, so the bot can see pasted links. Turn off **Public Bot**, so only you can add it to servers.
3. Click *Reset Token* there and paste the token into PrikBoard, then click *Connect*.

What happens when the token is saved:

- **The check.** setDiscordBot first asks Discord about the token (`DiscordApplications`, `GET /applications/@me`). A wrong token, or one without the Message Content intent, is a 400 on the admin page.
- **Storage.** The token is stored encrypted with Data Protection (`DiscordBotSettings.ProtectedBotToken`, purpose `PrikBoard.DiscordBot`), like sign-in provider secrets.
- **Connecting.** `DiscordBot.LoadAsync` hands the token to the gateway, which logs the old one out and the new one in, without a restart.
- **The public address.** This is the PrikBoard address links are matched against. It defaults to the address the admin is using and can be changed on the same page.

*Invite to a server* opens Discord's invite link, with scope `bot` and permissions `93184`. Those are View Channels, Send Messages, Embed Links and Read Message History, plus Manage Messages. Manage Messages is optional: it only lets the bot hide Discord's own "not signed in" preview above its reply.

The same section shows the bot's state:

- **Online**, **Connecting**, or **Failed** with the reason. For example, Discord closes the connection with 4014 when the intent is off, and 4004 when the token was reset.
- The servers the bot is in, with the boards previewed in each. *Leave* makes the bot leave a server and disconnects its boards.
- A warning while the bot is public.

getSystemChecks lists the bot once it is set up.

`Discord:BotToken` and `Discord:PublicUrl` in configuration still work and win over the admin page, which then shows them read-only. That mirrors `Auth:Providers` and sign-in providers.

PrikBoard runs as one instance, and that matters here too: two instances with the same token would each answer every message.

### The grant

Everyone who can read a channel sees every embed posted there, so access is granted to a Discord server (a *guild* in Discord's API), not to one person. The grant reads: *this server may see this board*.

Each connected guild is an `Agent` (issuer `discord`, `ClientId` = the guild id). It is added as a **Viewer** `BoardMember` of each board an owner connects. That reuses what already exists:

- The bot looks boards up as that agent (`BoardAccess.RoleOnBoardAsync`), so it can only read what the server was given.
- The connection shows in the board's members list, with the guild's name and a Discord badge. Removing that member disconnects the board.
- Discord servers are never assignees (`BoardAccess.AssignableIds`).

A `DiscordGuild` row keeps what the agent can't hold:

| Field | Meaning |
| --- | --- |
| `GuildId` | Discord's id for the server (a snowflake, kept as text) |
| `AgentId` | The guild's `Agent` |
| `ConnectedById` | The board owner who made the latest connection |
| `ConnectedAt` | When |

There is also a `DiscordBoardChannels` row per connected board, saying where the board may be previewed:

| Field | Meaning |
| --- | --- |
| `GuildId`, `BoardId` | The connection. The membership is the grant; this row only narrows it. |
| `Channels` | The channels (id and name) previews of this board are limited to, or null for the whole server |

Discord's own ids are not personal data about PrikBoard users, so storing them in plain text is fine. No Discord user ids are stored.

### Connecting a board (owners)

In the board menu, *Integrations* (owners only) opens `IntegrationsDialog`. It lists the servers previewing the board, with each one's channels as chips and a *Disconnect* button. Below the list:

1. Pick a server the bot is in. These come from listDiscordServers, which reads the gateway's cache.
2. Pick one of its text channels, or *Whole server*. Only channels where the bot may read and post are offered.
3. Click *Connect* (connectDiscordChannel).

What the choices do:

- **Another channel** adds to the list. A board takes at most 50 channels of one server (`DiscordBoardChannels.MaxChannels`), and a 51st is a 409.
- **Whole server** replaces the list.
- **A channel picked for a board already on the whole server** narrows it to that channel ("Limit to channel").
- **Removing a channel chip** takes that channel away. Removing the last one disconnects the server.

The server and channel names come from the bot, not from the request, so a board can only be connected to a channel the bot really is in. Connecting is for browser sessions only (403 for tokens), because the connection records who made it.

Who may connect what: any owner may connect their own board to any server the bot is in. The board's content is theirs to share. The servers are the ones the bot's owner put it in, which is why the admin page says to turn off *Public Bot*. Admins can make the bot leave a server.

If no admin has set up a bot, the dialog says so: admins get a link to the admin page, and other people are asked to contact an admin. If the bot is in no server yet, the dialog says that instead.

### Previews

For every message, the bot works through these steps:

1. It ignores messages from bots, including its own, and messages outside servers.
2. It finds candidate links with a loose match on the public address (`LinkFinder`, up to five per message). A link wrapped in `<…>` is skipped, since that is Discord's way of posting a link without a preview.
3. It looks each link up with `LinkPreviews.FindAsync` (guild, link, channel, public address). Each link counts against a limit per server (`RateLimits:LinkPreviewsPerMinute`, 60), so one busy server can't use up the others' previews.
4. It posts one reply with an embed per link that got a preview (`Embeds`) and stays silent about the rest. Saying "no access" would confirm that the board exists.
5. With *Manage Messages*, it removes Discord's own anonymous preview from the message it answered (suppress embeds).

A message in a thread counts as being in the thread's channel.

`LinkPreviews.FindAsync` returns one of:

| `Kind` | When | Fields |
| --- | --- | --- |
| `Card` | A card link (either path form, `CardLinks.TryParse`) on a board the guild's agent can read, in an allowed channel | `ReadableId`, `Title`, `Description`, `Color`, `BoardName`, `Path` |
| `Board` | A board link (`/#/boards/{shortId}` or `/#/boards/{boardId}`) the agent can read, in an allowed channel | `BoardName`, `Description`, `Path` |
| `Invite` | `/#/invite/{token}` for an open invite | `BoardName`, `Role`, `InvitedBy`, `ExpiresAt` |

Everything else gets nothing, and it all looks the same:

- a link to another host
- a board or card the guild can't see, or that doesn't exist
- a used, revoked or expired invite
- a server that isn't connected to anything
- a channel outside the board's `Channels`

The individual rules:

- **Card description.** Cards have no description field. The description is the first Text component on the card's canvas, top to bottom and then left to right. Its Markdown is reduced to plain text and cut to 300 characters at a word, with an ellipsis. A card whose content is only checklists gets `3/7 done` instead (`CardPreview.Describe`).
- **Board description.** `Board.Description`, cut the same way, or nothing.
- **Invites** reuse `previewInvite`'s lookup and are never spent. They need no membership or channel, since anyone holding the link is meant to see the board's name. They do need a server where the bot is connected to some board.
- **Host check.** A link counts only when its origin and path match the public address, so a look-alike host isn't previewed as PrikBoard.

### What the embeds show

All three use the PrikBoard icon and name as author, so they read as the same thing as the anonymous preview.

- **Card**: the title is `PRIK-109 · <card title>`, linking to the card. Then the description, when there is one. The footer is the board name and the color is the card's fill.
- **Board**: the title is the board name, linking to the board. Then the description, when there is one. The color is the logo's yellow.
- **Invite**: the title is *You're invited to join &lt;board name&gt;*, linking to the invite. Then *Daniel invited you as Editor · expires 28 Oct*, and a line saying the link works once, for whoever accepts it first. There is no board description: an invite shows only what `previewInvite` shows.

## Open questions

1. ~~**Bot credential.**~~ Decided, then changed. The first version gave each guild a `pb_pat_` token through a PKCE paste flow, and the bot ran on its own. Now the bot runs in the API with one token that an admin sets, and guilds need no credential: the bot looks links up as the guild's agent.
2. ~~**Invites in public channels.**~~ Decided: invites preview in any channel the bot posts in, with the line saying the link works once, for whoever accepts it first.
3. ~~**Channel limits per board or per server?**~~ Decided: per board. Owners pick the channels, or the whole server, under *Integrations*.
4. ~~**Who may connect a server?**~~ Decided: any board owner, to any server the bot is in. The bot's owner decides which servers those are (Public Bot off), and admins can make it leave one.
5. ~~**More on the card embed.**~~ Decided: none to start. Bucket, tags and assignees can come later as inline fields.
6. ~~**Readable ids in plain text.**~~ Decided: not now, full links only. Text such as `ISO-9001` looks like an id, and keys aren't unique.
7. ~~**Per-person check.**~~ Decided: no. The whole channel sees the embed, so the server grant and the channel limits are what protect a board.
8. ~~**Revoked access.**~~ Decided: embeds already posted stay, as snapshots. The *Disconnect* dialog says so. The bot keeps no record of its replies.
9. ~~**Images.**~~ Decided: none at first. Later, the bot could attach the card's first image to the reply (`attachment://`).
10. **Hosting.** Nothing is deployed yet. Discord's crawler needs a public host, and the bot is only useful when the public address is one people can open.
11. ~~**Later: notifications.**~~ Out of scope here, for a design of its own. With the bot in the API, it could post board changes from `BoardEvents` directly. Who picks which changes are posted, and where, is still open.
