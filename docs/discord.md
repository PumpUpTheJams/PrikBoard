# Design: Discord link previews

Status: built (PRIK-142). Part 1 is the meta tags in `src/web/index.html` and the PNGs in `src/web/public` (sources in `src/web/icons`); Part 2 is `Endpoints/DiscordEndpoints.cs` in the API, `components/DiscordConnectPage.tsx` and `DiscordDialog.tsx` in the web app, and the bot in `src/PrikBoard.Discord`. Not tried against Discord yet: that needs a public host (open question 10).

Where the build differs from the text below: the connect page is `/#/connect/discord?code_challenge=…`; owners can also remove one channel (removeDiscordChannel); getLinkPreview has its own rate limit per server (`RateLimits:LinkPreviewsPerMinute`, 60) rather than `InviteEndpoints.InviteLimit`; and connected servers are left out of the people cards can be assigned to.

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
- **The integration only talks to the API.** The Discord bot is a separate process that calls the PrikBoard REST API (`/api/...`) with a token per Discord server, like any other client. It has no database access and doesn't read the web app. The API, in turn, never calls Discord.

## Why the chat app's own preview sees so little

Chat apps build link previews by fetching the URL themselves, anonymously, and nobody sends the part after `#` to the server. Every PrikBoard link reaches the server as `/`, and the crawler isn't signed in. So the chat app's own preview can only ever be one fixed preview for all PrikBoard links, built from `index.html`.

The integration avoids both limits: the bot reads the link from the message text, fragment included, and asks the API about it with that server's token.

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
- Check that the API serves `index.html` for `/` with the tags (it does today, from the image's `wwwroot`) and that the PNGs are reachable without signing in (they are: they are static files, served before authentication).
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
| `ConnectedById` | The PrikBoard user who confirmed the latest connection |
| `ConnectedAt` | When |

And a `DiscordBoardChannels` row per connected board keeps where it may be previewed:

| Field | Meaning |
| --- | --- |
| `GuildId`, `BoardId` | The connection (the membership is the grant; this row only narrows it) |
| `Channels` | The channels (id and name, as Discord gave them at connect time) previews of this board are limited to, or null for the whole server |

Discord's own ids are not personal data about PrikBoard users, so storing them in plain text is fine. No Discord user ids are stored.

### Connecting a server

This is the VS Code extension's sign-in (`AppSignInEndpoints`, `components/ConnectPage.tsx`) with the paste path: an authorization code with PKCE, where the person carries the code back by hand.

1. A Discord admin runs `/prikboard connect` in a channel of the server. The bot checks that they have the *Manage Server* permission, which the interaction payload includes. It makes a PKCE code verifier, keeps it in memory for that guild for 10 minutes together with the channel's id and name, and replies with a private (ephemeral) message linking to `/#/connect?app=discord&challenge=...`. The bot makes no API call yet.
2. A board owner opens the link, signs in and picks the boards to connect, and for each one *only the channel where `/prikboard connect` was run* (the default) or *the whole server*. The page can't name the channel either; the bot shows its name in step 4. The page names no server: anything in the link could be forged, so it says *paste the code into the Discord server where `/prikboard connect` was run*.
3. `approveDiscordConnect` (session only, Owner on each chosen board, `SameOrigin`, like approveAppSignIn) stores a one-time code with the challenge, the boards and each board's channel choice (hashed, 5 minutes) and the page shows it.
4. The admin runs `/prikboard finish code:<code>` in the same server (ephemeral, *Manage Server* again). The bot takes the guild id and name from the interaction, which Discord vouches for, not from the link, and calls `exchangeDiscordConnect` (anonymous and rate limited, like exchangeAppSignIn) with the code, its verifier, the guild id and name, and the channel kept from step 1. It replies with the boards and channels that are now connected.
5. The API creates or reuses the guild's agent, adds it as a Viewer to the boards, adds the channel to each board's `Channels` (or sets it to null for *the whole server*), spends the code and returns a new `pb_pat_` token of kind `Discord` once. The bot stores it for that guild, encrypted at rest. A code started in one server can't be finished in another: the other server has no matching verifier.

Both sides consent: a Discord admin for the server, and a PrikBoard owner for each board. Connecting more boards, or allowing a board in another channel, runs the same flow from that channel; the API adds the memberships and the new token replaces the guild's old one. `/prikboard status` lists the connected boards.

### How the bot calls the API

With the guild's own token, `Authorization: Bearer pb_pat_...`, like any other client. There is no bot-wide credential and no guild header: the token says which guild it is.

- **Token kind `Discord`** (`AccessTokenKind`, next to `Agent` and `App`). `AgentId` is the guild's agent and `OwnerId` the owner who approved it (it counts toward their `MaxPerOwner` and shows in their token list). `AccessTokenHandler` gives it the agent as principal and scope `boards:read` only, and no owner cap: `BoardCaller` uses the agent's own memberships, as for an issuer's agent. So the owner who connected it leaving the board doesn't break it; removing the guild's membership does.
- **Lifetime.** `AccessToken.MaxDays` (365). `/prikboard status` warns from 14 days before it ends, and connecting again renews it.
- **Revoking.** Removing the guild from the board's members, revoking the token in the owner's token list, or `/prikboard disconnect` (the bot calls `revokeDiscordToken` with the token itself). A guild without a token gets no previews; the bot doesn't call the API for it.
- **What a leak costs.** A stolen bot store gives each connected guild's read-only token, for that guild's boards only, not a key to every server.

For previews the bot makes one call per link, to a new endpoint that does the parsing and picks what to show, so the bot stays thin and the rules live in one place:

`POST /api/link-previews` (`getLinkPreview`, `.NoBoardScope()`, `Discord` tokens only) with `{ "url": "<the link as pasted>", "channelId": "<where it was pasted>" }`. The link goes in the body, not the path, because an invite link holds its token. It answers with one of:

| `kind` | When | Fields |
| --- | --- | --- |
| `Card` | A card link (either path form, `CardLinks.TryParse`) on a board the guild's agent can read | `readableId`, `title`, `description`, `color`, `boardName`, `path` |
| `Board` | A board link (`/#/boards/{shortId}` or `/#/boards/{boardId}`) the agent can read | `boardName`, `description`, `path` |
| `Invite` | `/#/invite/{token}` for an open invite | `boardName`, `role`, `invitedBy`, `expiresAt` |

Everything else is a 404: a link to another host, a board or card the guild can't see or that doesn't exist, a used, revoked or expired invite, a board or card link from a channel outside that board's `Channels`. They all look the same, so the bot can't be used to find out which boards exist.

- **Card description.** Cards have no description field, so it is the first note (Text component) on the card's canvas (top to bottom, then left to right), Markdown reduced to plain text and cut to 300 characters at a word, with an ellipsis. A card without notes has none, and a card whose content is a checklist gets `3/7 done` instead. Built by a `CardPreview.Describe(card)` helper so the rule can be tested on its own.
- **Board description.** `Board.Description`, cut the same way, or null.
- **Invites** reuse `previewInvite`'s lookup and never spend the token. They need no membership, since anyone holding the link is meant to see the board's name, but only a `Discord` token can ask, and that already takes a connected guild, so it can't be used to test invite tokens at scale. Rate limited with `InviteEndpoints.InviteLimit`, keyed per guild.
- The invite response has no `yourRole`: the bot can't tell who will click.
- **Host check.** Links are only parsed when their origin is the API's own public URL (`Discord:PublicUrl`), so a look-alike host isn't previewed as PrikBoard.

No MCP tool: it exists for the bot, like token management exists for people. If agents want it later, invites would need a rule first, since `previewInvite` is people only.

### The bot

A new project, `src/PrikBoard.Discord`: a .NET worker using a Discord library (Discord.Net or NetCord) and a compose service next to the API. It reaches the API over HTTP only (`PrikBoard:ApiUrl`).

- It connects out to Discord's gateway, so it needs no public inbound port. Slash commands come over the same connection.
- It needs the **Message Content** intent to read pasted links. Discord treats it as privileged: free for bots in under 100 servers, verification by Discord above that.
- It finds candidate links with a loose match on the configured host and sends each to `getLinkPreview`; the API decides what they are. Up to five links per message.
- It posts one reply per message, with one embed per link that got a preview (Discord allows 10), and stays silent about the rest. Saying "no access" would confirm that the board exists.
- It sends the channel id with every `getLinkPreview` call; the API checks it against the board's `Channels` (invites aren't limited by channel).
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
- A *Discord* entry in the board menu, for owners: connected servers, their channel limits, and a remove button (with `ConfirmDialog`, which says previews already posted stay in Discord).
- The guild agent shows in the members list with a Discord badge.
- Mirrors in `api/types.ts` and `api/client.ts` for the endpoints the web uses (not `getLinkPreview`).

### API checklist

- `DiscordGuild` entity and migration; an `Agent` issuer `discord`.
- Endpoints, each with `WithName`, summaries and `RequireBoardRole`/`NoBoardScope`: `approveDiscordConnect` (session), `exchangeDiscordConnect` (anonymous, rate limited), `getDiscordStatus`, `getLinkPreview` and `revokeDiscordToken` (Discord token), `listDiscordConnections` and `removeDiscordConnection` (board Owner).
- `CardLinks` gains a board-link parser next to `TryParse`; `CardPreview.Describe`.
- `AccessTokenKind.Discord` in `AccessTokenHandler` and `BoardCaller`; the connect page handles `app=discord`.
- Configuration: `Discord:PublicUrl` (the PrikBoard origin links are matched against). `Discord:BotToken` belongs to the bot only.
- Tests: connect flow (code expiry, reuse, non-owner); `getLinkPreview` for a card with and without text, a checklist card, a board with and without a description, an open invite, and identical 404s for an unknown board, a board the guild can't see, a removed connection, an unknown guild, another host and a used invite; previewing an invite doesn't spend it; the description cut.
- Docs: `api.md` endpoint table, `authentication.md` (the bot as a caller).

## Order of work

1. Part 1: meta tags and images. Useful in every chat app, no Discord setup.
2. Part 2.

## Open questions

1. ~~**Bot credential.**~~ Decided: a `pb_pat_` token per guild, of kind `Discord`, from the VS Code-style PKCE sign-in (see Connecting a server). No bot-wide credential.
2. ~~**Invites in public channels.**~~ Decided: preview invites in any channel the bot posts in, with the line saying the link works once, for whoever accepts it first. Invites aren't tied to a board the guild is connected to, so channel limits don't apply to them.
3. ~~**Channel limits: in the bot or in the API?**~~ Decided: in the API. `getLinkPreview` takes the `channelId`, and a channel that isn't allowed gets the same 404 as a board the guild can't see. The bot reports the channel, so this catches bot bugs, not a compromised bot (which holds the token anyway); the rules stay in one place.
4. ~~**Channel limits per board or per server?**~~ Decided: per board, taken from the channel where `/prikboard connect` was run, or the whole server, chosen on the connect page. Another channel is added by connecting again from it; owners remove channels in the board's *Discord* menu.
5. ~~**More on the card embed.**~~ Decided: none to start. The embed shows the readable id, title, description, board name and color only; bucket, tags and assignees can come later as inline fields.
6. ~~**Readable ids in plain text.**~~ Decided: not now, full links only. Text such as `ISO-9001` looks like an id and keys aren't unique. If it comes later, it matches only keys of boards connected to the server, in their allowed channels, and an ambiguous key previews nothing.
7. ~~**Per-person check.**~~ Decided: no. The whole channel sees the embed, so the server grant and the channel limits are what protect a board; checking the poster would add Discord sign-in and account linking for almost no privacy gain.
8. ~~**Revoked access.**~~ Decided: embeds already posted stay; they are snapshots. The owner's *Disconnect* dialog says "Previews already posted in Discord stay there. Delete them in Discord if needed." The bot keeps no record of its replies.
9. ~~**Images.**~~ Decided: none at first. If they come later, the bot downloads the card's first image with its token and attaches it to the reply (`attachment://`), so no signed URLs or anonymous file routes are needed.
10. **Hosting.** Nothing is deployed. Both parts need a real public host: crawlers can't reach `localhost`, and the bot needs the API reachable. This waits on HTTPS and persisted Data Protection keys (see `authentication.md`).
11. ~~**Later: notifications.**~~ Decided: out of scope here, for a design of its own. The live hub (`/api/live`) already accepts the guild's read-only token, so the bot could listen as the guild's agent, but a `BoardChanged` event doesn't say what changed: the bot would have to compare board versions itself, or the API would need richer events. Who picks which changes are posted, and where, is open.
