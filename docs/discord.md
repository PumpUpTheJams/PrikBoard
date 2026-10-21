# Design: Discord link previews

Status: proposal, nothing built.

## Goal

When someone pastes a PrikBoard link in Discord (or Slack, Teams, WhatsApp), the chat should show what it points to. Boards are members-only, so this comes in two layers:

1. **Anonymous previews** (every chat app, no setup). The link expands to the PrikBoard icon and a short fixed text about what kind of link it is: a post-it or a board. It never shows anything from the board, so it is safe whether or not the reader, or the chat app's crawler, is signed in.
2. **Rich previews** (Discord only, opt-in). In a Discord server a board owner has connected, a bot replies with the post-it's readable id, title, bucket, tags, assignees and a short excerpt.

![Mockup: the anonymous preview, its large-image variant, and the bot's embed](discord-previews.svg)

## Why the chat app's own previews see so little

Chat apps build link previews by fetching the URL themselves, anonymously. Two things limit that:

1. **No access.** The crawler gets the sign-in page, never the board.
2. **No fragment.** PrikBoard links put everything in the hash (`/#/boards/{shortId}/notes/{number}`), and nobody sends the part after `#` to the server. Every link reaches the server as `/`, so all of them would get the same preview.

Layer 1 fixes the second problem with share links the server can see, and accepts the first: the preview says what kind of link it is, not what is on the board. Layer 2 avoids both, because a bot reads the message text itself and fetches the note with its own credentials.

## Layer 1: anonymous previews

### Share links

Two new paths, outside the hash, that need no lookup to turn into today's hash route:

| Share link | Opens |
| --- | --- |
| `/b/{shortId}` | `/#/boards/{shortId}` |
| `/b/{shortId}/n/{number}` | `/#/boards/{shortId}/notes/{number}` |

They use the board's short id rather than the readable id (`PRIK-109`), because board keys aren't unique and resolving one needs a signed-in caller. The hash links stay valid everywhere; these are only what the copy-link buttons hand out.

### The preview page

`GET /b/{shortId}` and `GET /b/{shortId}/n/{number}` are answered by the API (`Endpoints/SharePreviewEndpoints.cs`, `.AllowAnonymous()`, `.NoBoardScope()`, excluded from the OpenAPI document since they return HTML, not JSON). The response is a tiny, cacheable HTML page:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Post-it 109 on PrikBoard</title>
  <meta name="description" content="A post-it on a PrikBoard board. Open it to see the details. You need to be a member of the board.">
  <meta property="og:site_name" content="PrikBoard">
  <meta property="og:type" content="website">
  <meta property="og:title" content="Post-it 109 on PrikBoard">
  <meta property="og:description" content="A post-it on a PrikBoard board. Open it to see the details. You need to be a member of the board.">
  <meta property="og:url" content="https://prikboard.example/b/7KX2QD/n/109">
  <meta property="og:image" content="https://prikboard.example/og-image.png">
  <meta property="og:image:width" content="1200">
  <meta property="og:image:height" content="630">
  <meta property="og:image:alt" content="The PrikBoard post-it logo">
  <meta name="twitter:card" content="summary">
  <meta name="theme-color" content="#ffd54f">
  <link rel="icon" href="/favicon.svg">
  <meta http-equiv="refresh" content="0; url=/#/boards/7KX2QD/notes/109">
</head>
<body><a href="/#/boards/7KX2QD/notes/109">Open in PrikBoard</a></body>
</html>
```

- **No database lookup.** The page is built from the path alone. A board that doesn't exist, one you can't see and one you can all give the same preview, so share links can't be used to find out which boards exist. The web app does the access check after the redirect, as it does today (sign-in, then the board or `NoAccess`).
- **Validation.** `shortId` must be 6 Crockford base32 characters and `number` a positive integer; anything else is a plain 404. Every value written into the HTML is HTML-encoded.
- **Redirect in the page, not a 302.** A 302 to `/#/…` would make crawlers preview the SPA's `index.html` instead. Browsers follow the `meta refresh` at once; the link is there for those that don't. The page sends `Cache-Control: public, max-age=3600`, the same `X-Frame-Options`/`frame-ancestors` headers as the app, and `Referrer-Policy: no-referrer`.
- `og:url` and the image URL are absolute, built from the request's scheme and host (forwarded headers are already trusted, see `infra/README.md`).

### What the preview says

| Link | Title | Description |
| --- | --- | --- |
| Post-it | `Post-it 109 on PrikBoard` | A post-it on a PrikBoard board. Open it to see the details. You need to be a member of the board. |
| Board | `A PrikBoard board` | A post-it board for planning small projects. Sign in to open it. |
| Home (`/`) | `PrikBoard` | Post-it boards for small projects, for people and AI agents. |

The home page gets its tags as static meta tags in `src/web/index.html`, so a plain `https://prikboard.example` link expands too.

The post-it number is the only thing from the link that is shown. The readable id (`PRIK-109`) would read better, but the key is a name an owner chose, and showing it would need a lookup.

### Image and colour

- `src/web/public/og-image.png`, 1200 × 630: the post-it logo on the Night shift background with "PrikBoard" and the tagline (card 2 in the mockup). Chat apps that show `summary` cards crop it to a square thumbnail, so the logo sits where a centre crop keeps it.
- `src/web/public/icon-512.png` (and `apple-touch-icon.png`): many crawlers don't render SVG, so `favicon.svg` alone isn't enough.
- `theme-color` `#ffd54f` (the logo's yellow) sets the colour of the bar beside Discord's embed.

### Routing

- **nginx** (`src/web/nginx/default.conf.template`): add `b` to the API location, `location ~ ^/(api|openapi|scalar|b)(/|$)`.
- **Vite** (`vite.config.ts`): proxy `'/b'` to the API like `/scalar`, so previews can be tried in development (`curl -s http://localhost:5173/b/7KX2QD/n/109`).

### Web

- The copy-link buttons (the readable id in the expanded post-it, the copy-link button on a post-it) copy the share link, built from `location.origin` (`board/links.ts`, a `shareLink(board, note?)` helper). The board menu gets *Copy link*.
- `NoteLinks.TryParse` and `board/links.ts` also read the share-link forms, so a share link pasted into a links block or text opens in place like the others. Block writes keep storing the hash path.

### Checklist

- `SharePreviewEndpoints` with the page above; a shared `SharePreview.Html(title, description, url, target)` builder.
- `NoteLinks.TryParse` reads `/b/{shortId}/n/{number}`; `api.md` documents the share links.
- `og-image.png` and PNG icons in `src/web/public`; meta tags in `index.html`.
- nginx and Vite proxy entries.
- Tests: the page for a note, a board, and a board that doesn't exist are identical apart from the path; bad ids are 404; values are encoded; no cookie or token is needed; `NoteLinks` parses share links.

## Layer 2: a gateway bot acting as one agent per Discord server

Everything below is for the rich embed (card 3 in the mockup).

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
| `ChannelIds` | Channels previews are limited to, or null for the whole server; per board (see open questions) |
| `ConnectedById` | The PrikBoard user who confirmed the connection |
| `ConnectedAt` | When |

Discord's own ids are not personal data about PrikBoard users, so storing them in plain text is fine. No Discord user ids are stored in this design.

### Connecting a server

This works like the VS Code extension's sign-in (`AppSignInEndpoints`, `components/ConnectPage.tsx`):

1. A Discord admin runs `/prikboard connect` in the server. The bot checks that they have the *Manage Server* permission, which the interaction payload includes.
2. The bot asks the API for a one-time connect code (`beginDiscordConnect`, bot credential, 15 minutes, stored as a hash like `ImageUpload` tokens). It replies with a private (ephemeral) message linking to `#/connect/discord?code=...`.
3. A board owner opens the link, signs in and picks boards (and channels, if limited). The page shows the server's name and icon so they know what they are granting.
4. `confirmDiscordConnect` (session only, Owner on each chosen board) creates or reuses the guild's agent, adds it as a Viewer to those boards and spends the code. The bot is told and confirms in Discord.

Both sides consent: a Discord admin for the server, and a PrikBoard owner for each board. Owners can connect more boards later from the board menu, or from `/prikboard connect` again.

### How the bot calls the API

The bot has one service credential (see open questions for which kind). Every call also names the guild it acts for, and the API turns that into the guild's agent as the principal. The credential alone can't read any board: a guild that isn't connected has no agent and no memberships, so every board call is a 404.

For previews the bot uses the existing `getNote` (with `ResolveNoteAsync`-style parsing of the link) and, where useful, `getBoard`. A smaller `previewNote` endpoint could return just what an embed shows, but isn't needed to start.

The bot also checks the channel against `ChannelIds` before posting. That is a Discord-side rule the API can't see, unless the bot passes the channel id too and the API enforces it (see open questions).

### The bot

A new project, `src/PrikBoard.Discord`: a .NET worker using a Discord library (Discord.Net or NetCord) and a compose service next to the API.

- It connects out to Discord's gateway, so it needs no public inbound port. Slash commands come over the same connection.
- It needs the **Message Content** intent to read pasted links. Discord treats it as privileged: free for bots in under 100 servers, verification by Discord above that.
- Its link parser mirrors `NoteLinks.TryParse`: the share links (`/b/{shortId}/n/{number}`) and both hash forms (`/#/boards/{shortId}/notes/{number}` and `/#/boards/{boardId}/notes/{noteId}`), as full links on the configured PrikBoard host. A board link on its own gets a smaller embed (name, bucket counts).
- It posts one reply per message, with up to a few embeds (Discord allows 10), and ignores links to boards the guild can't see without saying so. Saying "no access" would confirm that the board exists. The anonymous preview from layer 1 still shows in that case.
- It ignores messages from bots, including its own.

### What an embed shows

- Author: the PrikBoard icon (`icon-512.png`) and "PrikBoard", so it matches the anonymous preview.
- Title: `PRIK-109 · <note title>`, linking to the note's share link.
- Fields: bucket, tags, assignees (by display name).
- Description: the start of the first text block, as plain text, cut to a few hundred characters. Checklists as `3/7 done`.
- Footer: board name. Colour: the post-it's fill colour.
- No images and no avatars (see open questions).

### Web

- The connect page (`#/connect/discord`).
- A *Discord* entry in the board menu, for owners: connected servers, their channel limits, and a remove button (with `ConfirmDialog`).
- The guild agent shows in the members list with a Discord badge.
- Mirrors in `api/types.ts` and `api/client.ts`.

### API checklist

- `DiscordGuild` entity and migration; an `Agent` issuer `discord`.
- Endpoints, each with `WithName`, summaries and `RequireBoardRole`/`NoBoardScope`: `beginDiscordConnect` (bot), `getDiscordConnect` (session, preview for the connect page), `confirmDiscordConnect` (session), `listDiscordConnections` and `removeDiscordConnection` (board Owner).
- The bot auth scheme.
- No MCP tools, like token management.
- Configuration: `Discord:BotToken` (bot only), `Discord:PublicUrl` (the PrikBoard origin the bot matches links against), plus whatever the bot credential needs.
- Tests: connect flow (code expiry, reuse, non-owner), a guild agent only reads its boards, a removed connection gets 404, an unknown guild gets 404.
- Docs: `api.md` endpoint table, `authentication.md` (the bot as a caller).

### Simpler first step

Without any API changes: an owner creates a Viewer personal access token limited to one board and gives it to the bot with `/prikboard connect token:<pat>` (the reply is ephemeral). The bot stores it per guild and uses it as is.

It is enough to try the idea, but:
- it acts through the owner's membership, so it stops working when they leave the board or are removed;
- the bot stores a secret per server;
- members see it as the owner's agent, not as the Discord server.

## Order of work

1. Layer 1 on its own: share links, preview page, image, copy-link buttons. It is useful in every chat app and needs no Discord setup.
2. Layer 2's simpler first step, to try the bot.
3. Layer 2 in full.

## Open questions

1. **Opt-in titles in anonymous previews.** Should an owner be able to turn on "Show titles in link previews" for a board, so layer 1 shows the readable id, title and bucket to anyone with the link? It makes previews useful without a bot, but turns every share link into a public peek at the post-it, and needs the lookup layer 1 avoids. Off by default if it comes.
2. **Two previews in one message.** In a connected server, Discord shows the anonymous preview and the bot's embed. The bot can hide the first (suppress embeds) only with *Manage Messages*. Ask for that permission, or live with both?
3. **Bot credential.** A new auth scheme (a shared secret, with the guild id in a header) or a JWT issuer under `Auth:Tokens:Issuers` whose tokens carry the guild id as a claim? The scheme is simpler; the issuer fits what already exists and gives expiry for free.
4. **Channel limits: in the bot or in the API?** If the bot sends the channel id and the API checks it, a bug or a compromised bot can't post a board into the wrong channel. It also means the API learns about Discord channels.
5. **Channel limits per board or per server?** Per board is more precise (`#design` sees the design board only) but makes the settings page bigger.
6. **Readable ids in plain text.** Should the bot also preview a bare `MPCT-415` typed in a message? Keys aren't unique, so it would only look among the guild's boards, and it may fire on text that only looks like an id.
7. **Per-person check.** Should the bot only preview links the poster can open? That needs Discord as a sign-in provider (a hashed `UserIdentity`, as now) and the poster to link their account. The server grant is still needed, since the whole channel sees the embed.
8. **Revoked access.** Embeds already posted stay in Discord's history after a disconnect. Accept and document it, or have the bot edit its old replies away (it would have to remember them)?
9. **Images.** Discord fetches embed images anonymously, so an image needs a signed, short-lived URL. Worth it, or leave images out?
10. **Interactions over HTTP instead of the gateway.** Slash commands and a *Preview in PrikBoard* message command need no privileged intent and could run inside the API (with Ed25519 signature checks), but can't preview pasted links automatically, and need a public HTTPS endpoint.
11. **Hosting.** Nothing is deployed. Both layers need a real public host: crawlers can't reach `localhost`, and the bot needs the API reachable. This waits on HTTPS and persisted Data Protection keys (see `authentication.md`).
12. **Later: notifications.** With a guild agent in place, posting board changes to a channel (Discord webhooks, fed by the live update events) is a small next step. Is that wanted, and who picks which changes?
