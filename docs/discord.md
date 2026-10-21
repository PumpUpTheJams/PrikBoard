# Design: Discord link previews

Status: proposal, nothing built.

## Goal

When someone pastes a PrikBoard link in Discord, the chat should show what it points to, as far as it is allowed to. Three cases:

| Where | Link | Preview |
| --- | --- | --- |
| Any chat app, or a Discord server without the integration | Any PrikBoard link | Small: the PrikBoard icon with a *not signed in* badge, saying it is a PrikBoard link and that neither you nor the integration is signed in here. Nothing from the board. |
| A Discord server with the integration, for a board connected to it | Board or card | The board's name and description, or the card's readable id, title and description, when there is one. |
| A Discord server with the integration | Invite (from *Share*) | *You're invited to join &lt;board name&gt;*, with the role and expiry. |

![Mockup: the anonymous preview, and the integration's card, board and invite embeds](discord-previews.svg)

### Constraints

- **Links stay as they are.** Board and card links keep everything behind the `#` (`/#/boards/{shortId}/cards/{number}`), and so do invites (`/#/invite/{token}`). There are no new share links or server-rendered pages.
- **The integration only talks to the API.** The Discord bot is a separate process that calls the PrikBoard REST API (`/api/...`) with its own credential, like any other client. It has no database access and doesn't read the web app. The API, in turn, never calls Discord.

## Why the chat app's own preview sees so little

Chat apps build link previews by fetching the URL themselves, anonymously, and nobody sends the part after `#` to the server. Every PrikBoard link reaches the server as `/`, and the crawler isn't signed in. So the chat app's own preview can only ever be one fixed preview for all PrikBoard links, built from `index.html`.

The integration avoids both limits: the bot reads the link from the message text, fragment included, and asks the API about it with its own credential.

## Part 1: the anonymous preview

### Meta tags in `index.html`

`src/web/index.html` gets static meta tags. They are the same for every link, since every link is `/` to a crawler:

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

- **Small card.** `twitter:card` `summary` and a square image give the small thumbnail on the right of the card, not a large banner.
- **No `og:url`.** It would tell the chat app that every link is the home page; without it, the card links to what was pasted.
- **The image** (`src/web/public/og-signed-out.png`, 256 × 256): the card logo on the Night shift background with a small round badge (a person with a slash, or a lock) in a corner. Many crawlers don't render SVG, so it is a PNG. Add `icon-512.png` and `apple-touch-icon.png` there too; the bot uses the icon.
- **Relative image URL.** Discord and most crawlers resolve `og:image` against the page URL. If one doesn't, the build can write the absolute URL from a `PRIKBOARD_PUBLIC_URL` build variable; decide when there is a host.
- `theme-color` `#ffd54f` (the logo's yellow) colors the bar beside Discord's card.

The text is the same everywhere it appears: the home page, a board, a card, an invite. It never says which kind of link it is, because the server can't tell.

### Checklist

- Meta tags in `index.html`; `og-signed-out.png`, `icon-512.png` and `apple-touch-icon.png` in `src/web/public`.
- Check that nginx serves `index.html` for `/` with the tags (it does today) and that the PNGs are reachable without signing in.
- Try it with a public host (see Hosting): crawlers can't reach `localhost`.

## Part 2: the Discord integration

### The grant

Everyone who can read a channel sees every embed posted there, so access is granted to a Discord server (a *guild* in Discord's API), not to one person: *this server may see this board*.

Each connected guild becomes an `Agent` (issuer `discord`, `ClientId` = the guild id), added as a **Viewer** `BoardMember` of each board an owner connects. That reuses everything that already exists:

- `BoardAccess`, `BoardCaller` and `RequireBoardRole` work unchanged, and the agent can only read.
- The connection shows in the board's members list, with the guild's name as `DisplayName`.
- Removing that member disconnects the board. Deleting a board removes its memberships, as it does now.

A new `DiscordGuild` row keeps what the agent can't hold:

| Field | Meaning |
| --- | --- |
| `GuildId` | Discord's id for the server (a snowflake, kept as text) |
| `AgentId` | The guild's `Agent` |
| `ChannelIds` | Channels previews are limited to, or null for the whole server (see open questions) |
| `ConnectedById` | The PrikBoard user who confirmed the connection |
| `ConnectedAt` | When |

Discord's own ids are not personal data about PrikBoard users, so storing them in plain text is fine. No Discord user ids are stored.

### Connecting a server

This works like the VS Code extension's sign-in (`AppSignInEndpoints`, `components/ConnectPage.tsx`):

1. A Discord admin runs `/prikboard connect` in the server. The bot checks that they have the *Manage Server* permission, which the interaction payload includes.
2. The bot asks the API for a one-time connect code (`beginDiscordConnect`: bot credential, guild id and name, 15 minutes, stored as a hash like `ImageUpload` tokens). It replies with a private (ephemeral) message linking to `/#/connect/discord?code=...`.
3. A board owner opens the link, signs in and picks boards (and channels, if limited). The page shows the server's name so they know what they are granting.
4. `confirmDiscordConnect` (session only, Owner on each chosen board) creates or reuses the guild's agent, adds it as a Viewer to those boards and spends the code. The page says it is done. The API doesn't call Discord, so the bot doesn't post a confirmation; the admin can run `/prikboard status` to see the connected boards.

Both sides consent: a Discord admin for the server, and a PrikBoard owner for each board. Owners can connect more boards later from the board menu, or from `/prikboard connect` again.

### How the bot calls the API

The bot has one service credential (see open questions for which kind). Every call also names the guild it acts for, and the API turns that into the guild's agent as the principal. The credential alone can't read anything: a guild that isn't connected has no agent, so every call for it is a 404.

For previews the bot makes one call per link, to a new endpoint that does the parsing and picks what to show, so the bot stays thin and the rules live in one place:

`POST /api/link-previews` (`getLinkPreview`, `.NoBoardScope()`, Discord bot only) with `{ "url": "<the link as pasted>" }`. The link goes in the body, not the path, because an invite link holds its token. It answers with one of:

| `kind` | When | Fields |
| --- | --- | --- |
| `Card` | A card link (either path form, `CardLinks.TryParse`) on a board the guild's agent can read | `readableId`, `title`, `description`, `color`, `boardName`, `path` |
| `Board` | A board link (`/#/boards/{shortId}` or `/#/boards/{boardId}`) the agent can read | `boardName`, `description`, `path` |
| `Invite` | `/#/invite/{token}` for an open invite | `boardName`, `role`, `invitedBy`, `expiresAt` |

Everything else is a 404: a link to another host, a board or card the guild can't see or that doesn't exist, a used, revoked or expired invite. The four look the same, so the bot can't be used to find out which boards exist.

- **Card description.** Cards have no description field, so it is the first text component on the card's canvas (top to bottom, then left to right), Markdown reduced to plain text and cut to 300 characters at a word, with an ellipsis. A card without text components has none, and a card whose content is a checklist gets `3/7 done` instead. Built by a `CardPreview.Describe(card)` helper so the rule can be tested on its own.
- **Board description.** `Board.Description`, cut the same way, or null.
- **Invites** reuse `previewInvite`'s lookup and never spend the token. They need no membership, since anyone holding the link is meant to see the board's name, but the guild must be connected to at least one board, so the bot's credential alone can't be used to test tokens. Rate limited with `InviteEndpoints.InviteLimit`, keyed per guild.
- The invite response has no `yourRole`: the bot can't tell who will click.
- **Host check.** Links are only parsed when their origin is the API's own public URL (`Discord:PublicUrl`), so a look-alike host isn't previewed as PrikBoard.

No MCP tool: it exists for the bot, like token management exists for people. If agents want it later, invites would need a rule first, since `previewInvite` is people only.

### The bot

A new project, `src/PrikBoard.Discord`: a .NET worker using a Discord library (Discord.Net or NetCord) and a compose service next to the API. It reaches the API over HTTP only (`PrikBoard:ApiUrl`).

- It connects out to Discord's gateway, so it needs no public inbound port. Slash commands come over the same connection.
- It needs the **Message Content** intent to read pasted links. Discord treats it as privileged: free for bots in under 100 servers, verification by Discord above that.
- It finds candidate links with a loose match on the configured host and sends each to `getLinkPreview`; the API decides what they are. Up to five links per message.
- It posts one reply per message, with one embed per link that got a preview (Discord allows 10), and stays silent about the rest. Saying "no access" would confirm that the board exists.
- It checks the channel against `ChannelIds` before posting.
- It ignores messages from bots, including its own.
- With *Manage Messages* it removes Discord's own anonymous link preview from the message it answers (suppress embeds), so the channel doesn't show "not signed in" right above a full preview. Without it, both show.

### What the embeds show

All three have the PrikBoard icon and "PrikBoard" as author, so they read as the same thing as the anonymous link preview.

- **Card**: title `PRIK-109 · <card title>` linking to the card; the description, when there is one; footer the board name; color the card's fill.
- **Board**: title the board name linking to the board; the description, when there is one; color the logo's yellow.
- **Invite**: title *You're invited to join &lt;board name&gt;* linking to the invite; *Daniel invited you as Editor · expires 28 Oct*; and a line saying the link works once, for whoever accepts it first. No board description: an invite shows what `previewInvite` shows, nothing more.

No images and no avatars (see open questions).

### Web

- The connect page (`#/connect/discord`).
- A *Discord* entry in the board menu, for owners: connected servers, their channel limits, and a remove button (with `ConfirmDialog`).
- The guild agent shows in the members list with a Discord badge.
- Mirrors in `api/types.ts` and `api/client.ts` for the endpoints the web uses (not `getLinkPreview`).

### API checklist

- `DiscordGuild` entity and migration; an `Agent` issuer `discord`.
- Endpoints, each with `WithName`, summaries and `RequireBoardRole`/`NoBoardScope`: `beginDiscordConnect` (bot), `getDiscordConnect` (session, for the connect page), `confirmDiscordConnect` (session), `getDiscordStatus` (bot), `getLinkPreview` (bot), `listDiscordConnections` and `removeDiscordConnection` (board Owner).
- `CardLinks` gains a board-link parser next to `TryParse`; `CardPreview.Describe`.
- The bot auth scheme.
- Configuration: `Discord:PublicUrl` (the PrikBoard origin links are matched against) and whatever the bot credential needs. `Discord:BotToken` belongs to the bot only.
- Tests: connect flow (code expiry, reuse, non-owner); `getLinkPreview` for a card with and without text, a checklist card, a board with and without a description, an open invite, and identical 404s for an unknown board, a board the guild can't see, a removed connection, an unknown guild, another host and a used invite; previewing an invite doesn't spend it; the description cut.
- Docs: `api.md` endpoint table, `authentication.md` (the bot as a caller).

### Simpler first step

Without any API changes: an owner creates a Viewer personal access token limited to one board and gives it to the bot with `/prikboard connect token:<pat>` (the reply is ephemeral). The bot stores it per guild and calls `getCard` and `getBoard` with it, building the description itself.

It is enough to try board and card embeds, but:
- invites don't work, since `previewInvite` is people only;
- it acts through the owner's membership, so it stops working when they leave the board or are removed;
- the bot stores a secret per server;
- members see it as the owner's agent, not as the Discord server.

## Order of work

1. Part 1: meta tags and images. Useful in every chat app, no Discord setup.
2. Part 2's simpler first step, to try the bot.
3. Part 2 in full.

## Open questions

1. **Bot credential.** A new auth scheme (a shared secret, with the guild id in a header) or a JWT issuer under `Auth:Tokens:Issuers` whose tokens carry the guild id as a claim? The scheme is simpler; the issuer fits what already exists and gives expiry for free.
2. **Invites in public channels.** An invite link is single-use and goes to whoever accepts it first. Should the embed only appear in channels limited by `ChannelIds`, or should the bot warn (as planned) and leave the choice to the poster?
3. **Channel limits: in the bot or in the API?** If the bot sends the channel id and the API checks it, a bug or a compromised bot can't post a board into the wrong channel. It also means the API learns about Discord channels.
4. **Channel limits per board or per server?** Per board is more precise (`#design` sees the design board only) but makes the settings page bigger.
5. **More on the card embed.** Bucket, tags and assignees fit as embed fields. Leave them out to start, as planned, or show them?
6. **Readable ids in plain text.** Should the bot also preview a bare `MPCT-415` typed in a message? Keys aren't unique, so it would only look among the guild's boards, and it may fire on text that only looks like an id.
7. **Per-person check.** Should the bot only preview links the poster can open? That needs Discord as a sign-in provider (a hashed `UserIdentity`, as now) and the poster to link their account. The server grant is still needed, since the whole channel sees the embed.
8. **Revoked access.** Embeds already posted stay in Discord's history after a disconnect. Accept and document it, or have the bot edit its old replies away (it would have to remember them)?
9. **Images.** Discord fetches embed images anonymously, so an image needs a signed, short-lived URL. Worth it, or leave images out?
10. **Hosting.** Nothing is deployed. Both parts need a real public host: crawlers can't reach `localhost`, and the bot needs the API reachable. This waits on HTTPS and persisted Data Protection keys (see `authentication.md`).
11. **Later: notifications.** With a guild agent in place, posting board changes to a channel is a small next step, but the API never calls Discord, so the bot would have to listen (the SignalR hub, as the guild's agent). Is that wanted, and who picks which changes?
