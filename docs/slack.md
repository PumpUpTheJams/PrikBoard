# Design: Slack integration

Status: built, and tried in a real workspace (2026-10-25): link previews that follow their cards, board updates, `/prikboard board`, Save to PrikBoard, and Sign in with Slack. It follows the Discord integration (docs/discord.md: the bot inside the API, *Integrations* per board, live previews, updates, Save to PrikBoard). Wherever Slack allows it, Slack does the same things in the same places.

## Setting it up

PrikBoard needs an https address that people (and their browsers) can reach, such as `https://board.example.com`. Slack itself never calls PrikBoard: the app connects out to Slack.

**Admin, once** (Admin → *Instance* → *Slack app*):
1. Check *Address people paste links from* and click **Save address**. The manifest takes the app's link domain and sign-in redirect from it.
2. Click **Create the Slack app**, pick your workspace and create it. If that link doesn't open Slack's form, copy the manifest from the page and use *Create New App → From a manifest* at api.slack.com/apps.
3. In the new Slack app: *Basic Information → App-Level Tokens → Generate Token and Scopes* with the scope `connections:write`, and copy the token (`xapp-…`). Then *Install App → Install to Workspace → Allow*, and copy the *Bot User OAuth Token* (`xoxb-…`).
4. Paste both tokens into PrikBoard and click **Connect**. The page should say **Online** within a few seconds.
5. Optional: *Sign in with Slack*. Admin → *Instance* → *Sign-in providers* → **Slack**, id `slack`, with the client id and secret from the Slack app's *Basic Information → App Credentials*. The manifest already has its redirect URL.

**Board owners**, per board:
1. In Slack, add the app to a channel: `/invite @PrikBoard`.
2. In PrikBoard, board menu → **Integrations** → *Slack*: pick the channel (or the whole workspace) and click **Connect**.
3. Click the channel to have it post the board's updates, and to pick the bucket *Save to PrikBoard* uses.

**Everyone** in a connected channel:
- Post a link to a card, board or invite: it gets a preview that keeps up with the card for a week.
- `/prikboard board` shows what is on the channel's board, only to you.
- *More actions → Save to PrikBoard* on a message turns it into a card made by you. The first time, the app asks you to connect your PrikBoard account (not when you sign in with Slack).

**If links don't get a preview**, but `/prikboard` and saving work: Slack sends no events to the app. In the Slack app, *Event Subscriptions* must be on, with `link_shared` under bot events and PrikBoard's host as unfurl domain; reinstall after changing it. More pitfalls are under *Tried in a real workspace* below.

## Goal

The same three things Discord does, for each connected Slack channel, set up by board owners under *Integrations*:

| Per connected channel | Slack | Default |
| --- | --- | --- |
| Previews | Card, board and invite links unfurl into a preview that keeps up with the card for a week | Always on |
| Updates | Posts the board activity the channel follows | Off |
| Save here | The *Save to PrikBoard* message shortcut saves to this board, in a bucket of the owner's choice | The first bucket |

The constraints stay the same:
- **Links stay as they are** (everything behind the `#`).
- **Set up while PrikBoard runs.** No redeploy and no second container. An admin pastes the Slack app's tokens, and board owners pick channels.

## How other tools do it, and what we take

| Tool | What they do | Here |
| --- | --- | --- |
| Linear, Jira, GitHub, Asana | Rich unfurls: Slack sends `link_shared`, the app answers with `chat.unfurl`. Lately they use Slack *Work Objects* too. | Phase 2 uses Block Kit. Work Objects come later. |
| GitHub (`/github subscribe org/repo issues`), Linear, Jira | A channel subscribes to a project and gets posts per change, with filters | Phase 3. These are Discord's *Updates*, set in *Integrations* rather than by slash command, so a board owner decides. |
| Linear (*Create issue* message shortcut), Jira, Asana, Trello | Turn a Slack message into an item, with a link back | Phase 4: *Save to PrikBoard* as a message shortcut with a modal |
| Linear, Jira | Per-user actions need a Slack user linked to an account. A *Connect account* button covers the first time. | Phase 4: the same link flow as Discord's *Who is who* |
| Linear, Jira | Buttons on unfurls (assign, change status), DMs for your own assignments, an App Home tab | Later |

## Where Slack differs from Discord

- **Two tokens instead of one.** Socket Mode needs an app-level token (`xapp-…`, scope `connections:write`) to open the WebSocket. The Web API calls need the bot token (`xoxb-…`). Socket Mode connects out, like Discord's gateway, so PrikBoard needs no public inbound URL. Slack only allows Socket Mode for apps that aren't in the Slack Marketplace, which suits one app per PrikBoard instance.
- **Unfurls instead of replies.** Slack sends `link_shared` only for the app's registered unfurl domains (up to five), and only the URLs. The app never reads messages: no `channels:history` and nothing like Discord's privileged Message Content intent. The preview is attached to the person's own message with `chat.unfurl`. There is no bot reply and no native embed to hide. Slack also sends the event while a link is being typed (`source: composer`), but without the channel, so PrikBoard leaves those alone (see Previews).
- **No bare ids at first.** A plain `PRIK-149` isn't a link, so Slack never sends `link_shared` for it. Previewing bare ids would need the `message.channels` event and `channels:history` (reading every message in the channels the bot is in). That is left for later (open question 3).
- **One workspace per install.** A Socket Mode app made for one instance is installed in that workspace (or a whole Enterprise Grid org). The model still keys everything by team id, so later more workspaces (an OAuth "Add to Slack" app) don't need a new model.
- **Slack Connect.** In channels shared with other organizations (`is_ext_shared`), unfurls and updates are seen by people outside the workspace. Such channels aren't offered in *Integrations*. If one becomes shared later, nothing is unfurled or posted there (`conversations.info`, cached).
- **Files need the bot token.** Save to PrikBoard fetches message images from `files.slack.com` with `Authorization: Bearer xoxb-…` (scope `files:read`), and only from that host.

## Shared model (phase 1, built)

Most of what the Discord code does isn't specific to Discord. Phase 1 separated the two, and Discord behaves the same as before (`DiscordApiTests` pass with only renames in the test code).

- **Domain** (`Domain/Chat.cs`):
  - `ChatWorkspace`, `ChatBoardChannels` (with `WholeWorkspace`), `ChatChannel`, `ChatUpdates`, `ChatAccount` and `ChatPreviewMessage` replace the Discord types. Each one has a `ChatPlatform` (`Discord`, `Slack`), stored as text.
  - Ids are `WorkspaceId`: a guild id, or a Slack team id.
  - The keys include the platform. A preview message is keyed by platform, channel and message, because Slack's message timestamps are only unique within a channel.
  - `ChatPlatforms` has the per-platform rules: `Issuer` and `IdMaxLength` (32).
  - `Agent.SlackIssuer = "slack"` exists, is reserved as a token issuer id, and is left out of assignees like `discord`.
  - `Domain/Discord.cs` keeps only `DiscordBotSettings` and `DiscordIds`.
- **Migration `ChatWorkspacesAndSlackApp`** (on top of the squashed `InitialCreate`; it also adds `SlackAppSettings`).
  - It creates the four chat tables, copies the Discord rows in with `Platform = 'Discord'`, then drops the old tables. Creating new tables keeps the keys and constraint names in line with the model.
  - Rolling back copies the Discord rows back; Slack rows have nowhere to go and are dropped.
  - `ChatWorkspacesMigrationTests` tests both directions, on a database leased from `TestDatabases` (`LeaseMissingDatabase`).
- **Lookups take the platform:**
  - `LinkPreviews.FindAsync` and `AgentIdAsync`;
  - `ChatPreviews` (was `DiscordPreviews`);
  - `ChatSaves.TargetsAsync` (was `DiscordSaves`);
  - `ChatAccounts` (was `DiscordAccounts`): the hash purpose is per platform (`discord-bot`, `slack-bot`), and so are link codes, so a code from one platform can't be redeemed through the other's endpoint.
- **Already neutral:** `LinkPreview` was already a plain record of what to show. `Embeds` stays the Discord renderer of it, and Slack gets `SlackBlocks` next to it.

Since then:
- **`ChatSaves.SaveAsync`** takes the message's platform (phase 4): the link back says "Message in Slack" or "Message in Discord", and images come from that app's own file hosts.
- **The OpenAPI schemas `DiscordUpdates` and `DiscordUpdate`** are now named `ChatUpdates` and `ChatUpdate` (phase 3 renamed the contract enum, since Slack channels follow the same updates). The fields and values are the same; the web app's type is `ChatUpdate` too.

## Connections (phase 2, built)

`Endpoints/ChatConnections.cs` is what connecting a board means for every chat app. The Discord endpoints use it now, and so do the Slack ones:
- connect: the workspace's agent joins the board as a Viewer, and its channel limits change;
- take a channel, the whole workspace, or the workspace away;
- forget a workspace;
- list a board's connections, and the admin page's boards per workspace.

Each endpoint group adds only what differs: which workspaces and channels its bot sees, and its own contracts. A shared `IChatGateway` wasn't needed for that, so there isn't one.

`SlackEndpoints` (board owners): `listSlackConnections`, `listSlackWorkspaces`, `connectSlackChannel`, `updateSlackChannel` (phase 3: the `updates` a channel posts; phase 4: its `saveBucketId`), `removeSlackConnection`, `removeSlackWholeWorkspace`, `removeSlackChannel`. They follow the same rules as Discord's: Owner, connecting is sessions only, at most 50 channels, and names come from Slack, not from the request. `ChatConnections.UpdateChannelAsync` is shared with updateDiscordChannel. Phase 4 added your own account link: `getSlackAccount`, `previewSlackAccountLink`, `linkSlackAccount` and `unlinkSlackAccount` (`/me/slack-account`, sessions only, linking from PrikBoard's own pages only).

## Setting up the Slack app (admins, phase 2, built)

Admin → *Instance* → *Slack app* (`components/SlackAppSettings`), next to *Discord bot*:

1. *Create the Slack app* opens `api.slack.com/apps?new_app=1&manifest_json=…` with the manifest PrikBoard fills in (`SlackApp.Manifest`):
   - Socket Mode on;
   - the unfurl domain set to the public address's host;
   - the event `link_shared`;
   - the slash command `/prikboard` (phase 3);
   - the message shortcut *Save to PrikBoard* (`callback_id` `save_to_prikboard`), with interactivity on and no request URL (phase 4);
   - bot scopes `links:read links:write channels:read groups:read chat:write commands users:read reactions:write files:read`;
   - Sign in with Slack's redirect URL, for a `Slack` sign-in provider with the id `slack` (no user scopes: Slack refuses to install an app that lists the sign-in's own).

   The address can be saved before the tokens (**Save address**), so the manifest names the right domain from the start. The page also shows the manifest to copy, in case the prefilled link doesn't work (phase 0, question 3). An app created from an older manifest needs what was added since (the command, the shortcut, interactivity, *Event Subscriptions* and the newer scopes) and reinstalling.
2. Install the app in the workspace and copy the *Bot User OAuth Token* (`xoxb-`).
3. Under *Basic Information* → *App-Level Tokens*, create one with `connections:write` (`xapp-`).
4. Paste both and click *Connect*. Then add the app to channels (`/invite @PrikBoard`): only those show up for board owners.

setSlackApp works like setDiscordBot:
- **Checks.** The bot token must start with `xoxb-` and pass `auth.test` as a bot. The app token must start with `xapp-` and open a Socket Mode connection; that is the only check Slack offers for it, and the connection is never used. Each failure is a 400 on its own field. Both tokens are needed the first time; after that, either can be replaced alone.
- **Storage.** Both are encrypted with Data Protection (purpose `PrikBoard.SlackApp`) in the `SlackAppSettings` row.
- **Connecting.** `SlackApp.LoadAsync` hands them to the gateway, which reconnects without a restart.

The section shows **Online**/**Connecting**/**Failed** (with the reason), the workspace and the boards previewed in it, and getSystemChecks lists the app once it is set up. `Slack:BotToken`, `Slack:AppToken` and `Slack:PublicUrl` in configuration win over the page, as for Discord. *Disconnect boards* (forgetSlackWorkspace) takes every board off a workspace; uninstalling the app happens in Slack. PrikBoard runs as one instance, which matters here too: two instances would both answer every event.

## The gateway (phase 2, built)

`Integrations/SlackGateway.cs` (`ISlackGateway`, a hosted service) uses `Integrations/SlackApi.cs`, a small client of our own instead of a Slack SDK:
- **Socket Mode.** It calls `auth.test` once, then `apps.connections.open`, and opens a `ClientWebSocket` to the address it gets. `hello` means Online.
  - Every envelope is acked with its `envelope_id` at once, and the work runs in the background, since Slack wants the ack within 3 seconds.
  - On a `disconnect` envelope (Slack refreshes connections every few hours) or a dropped socket, it opens a new connection, waiting 1 s at first and up to 30 s after repeated failures.
  - A refused token (`invalid_auth`, a token of the wrong kind, …) ends it as **Failed**, with a reason the admin page shows.
- **Web API.** Form POSTs with the token as bearer: `auth.test`, `apps.connections.open`, `conversations.list`, `conversations.info`, `chat.unfurl` and `chat.postMessage`. A slash command is answered with a JSON POST to its `response_url`, which needs no token; only Slack's `hooks.slack.com` (or the configured API's own host, for the fake) is used. `"ok": false` comes back as a `SlackApiException` with Slack's error code, and one rate limit of up to 30 s (`Retry-After`) is waited out.
- **The base address is configurable** (`Slack:ApiUrl`, default `https://slack.com/api/`), which is what lets the fake Slack stand in for Slack.
- **Channels for owners** are `conversations.list`, kept for a minute: the channels the bot is in, without Slack Connect ones.

## Previews (phase 2, built)

On `link_shared`, the gateway:

1. **Skips what it can't place.** Links typed in the message box come as `source: composer` with channel `COMPOSER`. Slack doesn't say which channel they are for, and whether a board previews depends on the channel, so those get nothing. This is a change from the first plan: previews appear once the message is posted, not while typing.
2. **Skips Slack Connect.** Channels shared with other organizations (`conversations.info`, `is_ext_shared`, kept for a minute) get nothing, whatever the board's connection says.
3. **Looks up each link.** For up to five links, `ChatPreviews.ForSourcesAsync` runs, which is `LinkPreviews.FindAsync` with the platform, team and channel. Slack shares the per-workspace rate limit with Discord's per-server one (`RateLimits:LinkPreviewsPerMinute`).
4. **Answers.** One `chat.unfurl` (`channel` + `ts`) with an entry for each link that got a preview. Links with no preview are left out: saying nothing doesn't confirm that a board exists.

5. **Remembers.** Once Slack takes the unfurl, the message is kept as a `ChatPreviewMessage` (platform Slack, the channel, the message's `ts` as its id, the links), for a week, so `ChatActivity` can unfurl it again. An edited message unfurled again replaces its row.

`SlackBlocks` draws a `LinkPreview` as an attachment, so the card's fill is the side bar, with Block Kit inside:
- **Card:** a section `*<link|PRIK-109 · title>*` with the description; fields for Bucket, Assigned, Tags, Checklist and 🤖 agent at work; and a context line with the PrikBoard icon and the board name.
- **Board:** the board name and description.
- **Invite:** *You're invited to join …*, who invited you, the role, the expiry as a Slack date (`<!date^…>`, shown in each reader's time zone), and the line saying it works once.

Text people typed is escaped (`&`, `<`, `>`), so a card title can't mention `@channel` or make a link.

## Live previews and updates (phase 3, built)

`Integrations/ChatActivity.cs` (was `DiscordActivity`) reads the card history every few seconds for both bots:
- it finds the remembered messages of changed cards (and of boards just disconnected, `RefreshBoard`);
- it gathers what each channel follows into one post per board;
- it builds each update as data, a `ChatUpdateLine`, not as Discord Markdown.

Each bot takes part through an `IChatActivityTarget` while it is online:
- **`DiscordActivityTarget`** edits replies and posts embeds, as before.
- **`SlackActivityTarget`** calls `chat.unfurl` again on the remembered message with the previews as they are now, or *No longer available* (`SlackBlocks.Unavailable`).
  - A message whose channel has since been shared with other organizations only gets *No longer available*.
  - A deleted message (`cannot_find_message`) is forgotten.
  - Real Slack replaces an existing unfurl when it is sent again (phase 0, question 2).
- **Updates** go out with `chat.postMessage`: one post per board after `Chat:UpdatesDelaySeconds` (30), with *Updates on &lt;board&gt;* as the notification text. The lines are the Discord bot's, in Slack's markup (`SlackBlocks.UpdateLine`).
  - Never into a channel shared with other organizations.
  - Owners pick what a channel follows by clicking its chip in *Integrations* (updateSlackChannel).
- **Settings.** `Chat:ActivitySeconds` and `Chat:UpdatesDelaySeconds` name the timings now; the older `Discord:` names still work.

`/prikboard board` (Socket Mode `slash_commands` envelopes) shows the board connected to the channel, only to whoever asked: `SlackBlocks.Snapshot`, a field per bucket with its first cards, agents at work, and the last 24 hours. It is posted to the command's `response_url` as an ephemeral message. A channel without a board, a channel shared with other organizations, and any other text each get a one-line answer instead.

## Save to PrikBoard and who is who (phase 4, built)

Under a message's *More actions*, *Save to PrikBoard* is a message shortcut. Slack sends it as an `interactive` envelope (`message_action`) with a `trigger_id` for one modal, which works for 3 seconds, and a `response_url` for answers only that person sees.

1. **Who it is.** `ChatAccounts.FindUserAsync(Slack, "{team}:{user}")`: Slack user ids are only unique within a workspace. The link holds only the keyed hash (`slack-bot`).
   - **Not known yet:** a modal (`SlackBlocks.NoticeView`) links to `#/connect/slack-account?code=…`, a code that works for 10 minutes.
   - On that page, `components/ChatAccountPage` (which was `DiscordAccountPage`, now for both apps) names the Slack account and warns to link only your own. The link is made with linkSlackAccount, and Account → *Slack* shows it and unlinks it.
   - Someone who signs in to PrikBoard with Slack (a `Slack` sign-in provider, *Sign in with Slack*) is known by that sign-in and never sees the connect link: the sign-in's subject is the same `{team}:{user}` key. See [authentication.md](authentication.md#slack).
2. **Where it can go.** `ChatSaves.TargetsAsync`: boards connected to this workspace that the person can edit, those in this channel first, each with the channel's save bucket (updateSlackChannel's `saveBucketId`) or its first bucket. With none, a modal says so.
3. **The modal** (`SlackBlocks.SaveView`) opens right away, as Slack's trigger requires. It has:
   - one select with a group per board and an option per bucket, the save bucket picked;
   - the title: the message's first line;
   - whose message it is.

   Reading the message goes on meanwhile and gets up to 1.5 s for the title to have names:
   - mentions become names (`SlackText.Plain`, with `users.info`);
   - links become Markdown;
   - its permalink (`chat.getPermalink`) and its files are looked up.
4. **Saving** (`view_submission`). The choice has to be one of the boards and buckets offered. `ChatSaves.SaveAsync` then creates the card as the person (`CardHistory.ActingAs`). The card gets:
   - the message quoted, with *From author in #channel, time*;
   - *Message in Slack* linking back;
   - up to four images, fetched from `files.slack.com` with the bot token (the fake's own host in tests). HttpClient drops the token on redirects.
5. **Telling them.** An answer only they see, through the shortcut's `response_url`: *Saved as PRIK-12 on Board*, with the card's preview, or why it wasn't saved. The bot reacts 📌 on the message.

Drafts wait 15 minutes, in memory (one instance, like the rest).

## Web (phases 2 to 4, built)

- `IntegrationsDialog` has a *Slack* section under *Discord* (`components/SlackIntegration`): the connected workspaces with a chip per channel and *Whole workspace*, *Disconnect* (with `ConfirmDialog`), and a channel picker that lists only channels the app is in and never Slack Connect ones. Clicking a channel's chip opens the updates it posts (`components/chatUpdates.ts` holds the choices both apps offer); a 🔔 shows it posts some. The same panel picks the bucket Save to PrikBoard puts cards in.
- Admin → *Instance* has *Slack app* (`components/SlackAppSettings`) under *Discord bot*: the steps, the manifest to copy, both tokens, the public address, the workspace with its boards.
- The members list shows a Slack badge for the `slack` issuer.
- Account has a *Slack* section next to *Discord* (`ChatAccount` in `components/AccountDialogs`), and `#/connect/slack-account` opens `components/ChatAccountPage` (`chatAccountLinkFromHash` in `components/discord.ts` reads both apps' links).

## Phases

0. **Spike** (done in a real workspace on 2026-10-25, see *Tried in a real workspace* below). The questions:
   1. Does `link_shared` give the URL with its `#/boards/…` part? **Yes**: Slack sent `https://…/#/boards/{shortId}/cards/2` in full, so no share links without a fragment are needed.
   2. Does a second `chat.unfurl` on the same message replace the unfurl? **Yes**: renaming a card changed its preview in Slack, so previews follow their cards for a week, as in Discord.
   3. Does the manifest prefill link work? Not tried: the app was made from the copied manifest.
   4. Which unfurl domain works for local testing? A Cloudflare quick tunnel (`cloudflared tunnel --url http://localhost:5173`, no account): Slack takes its `….trycloudflare.com` host as unfurl domain. Its address changes with every start.
1. **Shared chat model** (built). A refactor and migration, with Discord unchanged and its tests green.
2. **Slack app setup, gateway, previews, Integrations UI**, plus the fake Slack (built).
3. **Live previews and updates**, and `/prikboard board` (built).
4. **Save to PrikBoard and account linking** (built).
5. **Later:** bare ids (opt-in `channels:history`), buttons on unfurls (Assign me, Move to…, Mark done), DMs for your own assignments, an App Home tab with your cards, a daily digest, "Add to Slack" for several workspaces (OAuth, a public events URL), Work Objects.

## Testing (without a running Slack)

Slack can't run locally: there is no self-hosted or open-source Slack that runs Slack apps. Mattermost and Rocket.Chat copy Slack's webhooks, not Socket Mode, unfurls or modals. Testing therefore has four layers, and only the last needs Slack.

1. **Tests, no Slack** (CI, `dotnet test PrikBoard.slnx`). `SlackApiTests` runs the real `SlackGateway` against `tools/FakeSlack`, which `SlackApiFactory` starts on a free port, rather than faking the gateway. That way the Socket Mode and HTTP code is tested too. They cover:
   - the admin setup (wrong and wrong-kind tokens, encryption, removing);
   - reconnecting when Slack asks;
   - the system check;
   - owners connecting channels and the whole workspace, never channels the app isn't in or Slack Connect ones, and only owners in the browser;
   - unfurls only in the board's channels, never in Slack Connect channels or for composer links;
   - invites;
   - escaping;
   - previews unfurled again as their card changes, *No longer available* once disconnected, forgotten once deleted;
   - updates posted only for what a channel follows;
   - `/prikboard board` answered only to whoever asked, and the answers for a channel without a board, a Slack Connect channel and other text;
   - Save to PrikBoard: connecting the account (and only from PrikBoard's own pages), the modal with the channel's save bucket, the card made as the person with the quoted message, names for mentions, the link back and the image, the 📌, nowhere to save for a viewer, and asking again once unlinked;
   - `SlackText.Plain` on mentions, channels, links and escapes.

   The Discord tests still cover the shared lookups.
2. **Fake Slack, everything local** (`tools/FakeSlack`, a small ASP.NET app on http://localhost:5090). It plays the parts of Slack the app uses:
   - `/api/{method}` answers the Web API calls above like Slack (bearer tokens of the right kind, `"ok": false` with Slack's error codes), from a seeded workspace *Night owls*:
     - channels: `#general` and `#dev` with the app in them, `#random` without it, and `partners`, shared with another organization;
     - people: Ada (an admin) and Jimmy.
   - `/ws` is the Socket Mode socket. It sends `hello` and envelopes, takes acks, and can ask the app to reconnect.
   - The page at `/` is a pretend Slack. You post as one of its people, and links on the unfurl domain (`localhost` by default) produce `link_shared`. *Type only* sends the composer event instead. Unfurls show under the messages: Block Kit drawn roughly, with the raw JSON and a *Block Kit Builder* link.
   - `/_fake/messages`, `/_fake/calls`, `/_fake/composer` and `/_fake/disconnect` are what tests and curious people use.

   To try the whole flow:
   1. Start the `fake-slack` and `api-fake-slack` launch configs. The second is the API with `--Slack:ApiUrl=http://localhost:5090/api/`; `Slack__ApiUrl` works too.
   2. Start `web`, and sign in as an admin.
   3. Under Admin → *Instance* → *Slack app*, paste the fake's tokens (`xoxb-fake-bot-token`, `xapp-fake-app-token`, also shown on its page). Set the address to `http://localhost:5173` so the links you copy from PrikBoard are on the unfurl domain.
   4. Connect a board to `#general` under *Integrations*.
   5. Paste a card link in the fake's `#general`.
3. **See the messages.** Paste blocks JSON into Slack's Block Kit Builder (app.slack.com/block-kit-builder). It needs a free Slack account, not an app.
4. **Real Slack, free** (once for phase 0, and before release).
   - Create a free workspace (slack.com/get-started), or a free sandbox through the Slack Developer Program.
   - Give PrikBoard a public https address: a Cloudflare quick tunnel needs no account (`cloudflared tunnel --url http://localhost:5173`). Start the API and web app with the `api-tunnel` and `web-tunnel` launch configs, so sign-in callbacks through the tunnel are https and Vite accepts the tunnel's host. Its address changes with every start, and so would the app's unfurl domain and redirect URL.
   - Follow *Setting it up* above with the tunnel's address.
   - Connect a channel and try: a card link, a board link, an invite, a board you didn't connect (nothing), another channel (nothing), a Slack Connect channel (nothing), a change to the card (does the preview follow?), an update post, `/prikboard board`, and Save to PrikBoard on a message with an image, once before linking your account and once after.

   The fake can't answer phase 0's questions. It answers them the way we code it to. Only real Slack can. The same goes for anything else the docs leave open; the fake follows Slack's documentation.

## Tried in a real workspace (2026-10-25)

Everything worked against real Slack once the app was set up right: previews, `/prikboard board`, Save to PrikBoard (the card, the 📌, the answer only the saver sees) and Sign in with Slack. What tripped the setup up, for whoever does it next:

- **Event Subscriptions must be on.** Commands and shortcuts come over Socket Mode without it, but no events do, so links never unfurl while everything else works. With Socket Mode on, *Enable Events* asks for no Request URL; when it does, Socket Mode is off, and saving hangs waiting for a URL check. Subscribe to `link_shared` and list the unfurl domain there, then reinstall.
- **No user scopes for Sign in with Slack.** Listing `openid`, `profile` and `email` as *User Token Scopes* makes Slack refuse the install ("Invalid permissions requested"). The sign-in asks for them itself. The manifest no longer lists them.
- **Sign-in through a tunnel needs forwarded headers**, or the API builds `http://` callbacks that GitHub and Slack refuse (`api-tunnel` launch config, [authentication.md](authentication.md#behind-a-proxy-or-tunnel)).
- **A VPN's DNS filter can block tunnel services** (`trycloudflare.com` and `loca.lt` resolved to `0.0.0.0`).
- **A Slack sign-in that PrikBoard doesn't know needs an invite**, or linking from an account that is already signed in.
- **The `link_shared` log line** (`Slack link_shared in …: 1 link(s) on <host>`) tells at once whether Slack sent anything.

## Open questions

1. ~~**Fragments in `link_shared`**~~ Answered: Slack keeps them (phase 0).
2. ~~**Refreshing unfurls**~~ Answered: Slack replaces a message's unfurl when the app sends it again (phase 0).
3. **Bare ids.** Off, because it needs `channels:history`, which means reading every message in channels the bot is in. If we want it, make it a per-channel opt-in that an owner turns on, with the scope requested only then.
4. **Several workspaces.** Not with one Socket Mode app per instance. That needs an OAuth "Add to Slack" app with a public events URL, after there is a deployment (Discord's open question 10).
5. **Enterprise Grid.** An org-wide install gives events from many workspaces. The model keys on team id, so it should just work, but it is untested.
