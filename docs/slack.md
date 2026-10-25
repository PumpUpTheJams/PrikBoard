# Design: Slack integration

Status: phase 1 (the shared chat model) built; Slack itself is planned. It follows the Discord integration as it is on main (docs/discord.md: the bot inside the API, *Integrations* per board, live previews, updates, Save to PrikBoard). Wherever Slack allows it, Slack does the same things in the same places.

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
- **Unfurls instead of replies.** Slack sends `link_shared` only for the app's registered unfurl domains (up to five), and only the URLs. The app never reads messages: no `channels:history` and nothing like Discord's privileged Message Content intent. The preview is attached to the person's own message with `chat.unfurl`. There is no bot reply and no native embed to hide. When the link is typed in the composer, the event comes as `source: composer` with an `unfurl_id`, and the preview shows before sending.
- **No bare ids at first.** A plain `PRIK-149` isn't a link, so Slack never sends `link_shared` for it. Previewing bare ids would need the `message.channels` event and `channels:history` (reading every message in the channels the bot is in). That is left for later (open question 3).
- **One workspace per install.** A Socket Mode app made for one instance is installed in that workspace (or a whole Enterprise Grid org). The model still keys everything by team id, so later more workspaces (an OAuth "Add to Slack" app) don't need a new model.
- **Admin rights are in the API, not the payload.** Slash commands don't say whether someone is an admin, so where it matters the bot checks `users.info` (`is_admin`, `is_owner`).
- **Slack Connect.** In channels shared with other organizations (`is_ext_shared`), unfurls and updates are seen by people outside the workspace. Such channels aren't offered in *Integrations*. If one becomes shared later, nothing is unfurled or posted there (`conversations.info`, cached).
- **Files need the bot token.** Save to PrikBoard fetches message images from `files.slack.com` with `Authorization: Bearer xoxb-…` (scope `files:read`), and only from that host.

## Shared model (phase 1, built)

Most of what the Discord code does isn't specific to Discord. Phase 1 separated the two, and Discord behaves the same as before (`DiscordApiTests` pass with only renames in the test code).

- **Domain** (`Domain/Chat.cs`):
  - `ChatWorkspace`, `ChatBoardChannels` (with `WholeWorkspace`), `ChatChannel`, `ChatUpdates`, `ChatAccount` and `ChatPreviewMessage` replace the Discord types. Each one has a `ChatPlatform` (`Discord`, `Slack`), stored as text.
  - Ids are `WorkspaceId`: a guild id, or a Slack team id.
  - The keys include the platform. A preview message is keyed by platform, channel and message, because Slack's message timestamps are only unique within a channel.
  - `ChatPlatforms` has the per-platform rules: `Issuer`, `IsId` and `IdMaxLength` (32).
  - `Agent.SlackIssuer = "slack"` exists, is reserved as a token issuer id, and is left out of assignees like `discord`.
  - `Domain/Discord.cs` keeps only `DiscordBotSettings` and `DiscordIds`.
- **Migration `ChatWorkspaces`.**
  - It creates the four chat tables, copies the Discord rows in with `Platform = 'Discord'`, then drops the old tables. Creating new tables keeps the keys and constraint names in line with the model.
  - Rolling back copies the Discord rows back; Slack rows have nowhere to go and are dropped.
  - `ChatWorkspacesMigrationTests` tests both directions.
- **Lookups take the platform:**
  - `LinkPreviews.FindAsync` and `AgentIdAsync`;
  - `ChatPreviews` (was `DiscordPreviews`);
  - `ChatSaves.TargetsAsync` (was `DiscordSaves`);
  - `ChatAccounts` (was `DiscordAccounts`): the hash purpose is per platform (`discord-bot`, `slack-bot`), and so are link codes, so a code from one platform can't be redeemed through the other's endpoint.
- **Already neutral:** `LinkPreview` was already a plain record of what to show. `Embeds` stays the Discord renderer of it, and Slack gets `SlackBlocks` next to it.

Left for the phases that need them, since they can only be tried once a Slack gateway exists:
- **Endpoints** (phase 2). The Discord handlers query `ChatPlatform.Discord` directly. Phase 2 moves them into shared code behind a small `IChatGateway` (state, workspaces and channels the bot sees), mapped once per platform so operationIds stay explicit and the Discord ones don't change:
  - board connections: `listSlackConnections`, `listSlackWorkspaces`, `connectSlackChannel`, `updateSlackChannel`, `removeSlackConnection`, `removeSlackWholeWorkspace`, `removeSlackChannel`;
  - the admin's app setup: `getSlackApp`, `setSlackApp`, `deleteSlackApp`, `forgetSlackWorkspace`;
  - your own account link: `getSlackAccount`, `previewSlackAccountLink`, `linkSlackAccount`, `unlinkSlackAccount`.

  These copy the `RequireBoardRole`/`NoBoardScope` and `RequireAdmin` rules from the Discord endpoints. `docs/api.md`, `api/types.ts` and `api/client.ts` get them too.
- **`DiscordActivity` → `ChatActivity`** (phase 3). It reads the card history once and passes each preview edit and update post to the platform's gateway (`PostAsync`, `EditAsync`). Today it handles only Discord rows, and its update lines are Discord Markdown.
- **`ChatSaves.SaveAsync`** (phase 4). The link back says "Message in Discord", and images are only fetched from Discord's hosts.
- **The OpenAPI schema `DiscordUpdates`** is now named `ChatUpdates`. The fields and values are the same, and the web app's hand-written types don't use the schema name.

## Setting up the Slack app (admins, phase 2)

Admin → *Instance* → *Slack app*, next to *Discord bot*:

1. *Create the Slack app* opens `api.slack.com/apps` with a manifest PrikBoard fills in:
   - Socket Mode on;
   - the unfurl domain set to the public address's host;
   - the slash command `/prikboard`;
   - the message shortcut *Save to PrikBoard*;
   - the event `link_shared`;
   - bot scopes `commands links:read links:write chat:write channels:read groups:read users:read team:read reactions:write files:read`.

   The page also shows the manifest to copy, in case the prefilled link doesn't work (check this in phase 0).
2. Install the app in the workspace and copy the *Bot User OAuth Token* (`xoxb-`).
3. Under *Basic Information* → *App-Level Tokens*, create one with `connections:write` (`xapp-`).
4. Paste both and click *Connect*.

When the tokens are saved, setSlackApp works much like setDiscordBot:
- It checks the bot token with `auth.test` (team id and name) and the app token with `apps.connections.open`. Either failure is a 400.
- It stores both encrypted with Data Protection (purpose `PrikBoard.SlackApp`).
- It hands them to `SlackGateway` without a restart.

The section shows **Online**/**Connecting**/**Failed** (with Slack's reason), the workspace and the boards previewed in it. `Slack:BotToken`, `Slack:AppToken` and `Slack:PublicUrl` in configuration win over the page, the same as for Discord. `forgetSlackWorkspace` disconnects all boards; uninstalling the app itself happens in Slack. PrikBoard runs as one instance, which matters here too: two instances would both post every update.

## The gateway (phase 2)

`Integrations/SlackGateway.cs` (`ISlackGateway`, a hosted service) is a small client of our own, without a Slack SDK:
- **Socket Mode.** It calls `apps.connections.open`, opens a `ClientWebSocket`, reads envelopes and acks each one with its `envelope_id` within 3 seconds, then does the work in the background. On a `disconnect` envelope or a dropped socket it opens a new connection.
- **Web API** over `HttpClient`: `auth.test`, `team.info`, `conversations.list`/`conversations.info`, `users.info`, `chat.unfurl`, `chat.postMessage`, `chat.update`, `chat.postEphemeral`, `views.open`, `reactions.add`, and file downloads. It honors `Retry-After` on 429.
- **The base address is configurable** (`Slack:ApiUrl`, default `https://slack.com/api/`). This is what lets the fake Slack below stand in for Slack.

## Previews (phase 2)

On `link_shared`, the gateway:

1. **Filters the event.** It skips events from Slack Connect channels.
2. **Looks up each link.** For up to five links, `LinkPreviews.FindAsync(Slack, team, url, channel, publicUrl)`, with the same per-workspace rate limit as Discord (`RateLimits:LinkPreviewsPerMinute`).
3. **Answers.** One `chat.unfurl` with an entry for each link that got a preview. It uses `unfurl_id` + `source` for composer events, and `channel` + `ts` otherwise. Links with no preview are left out: saying nothing doesn't confirm that a board exists.
4. **Remembers.** It keeps the message (`ChatPreviewMessage`: channel, ts, sources) for a week, so phase 3 can refresh it.

`SlackBlocks` draws the `PreviewView`:
- **Card:** a section `*<link|PRIK-109 · title>*` with the description, fields (Bucket, Assigned, Tags, Checklist, 🤖 agent at work), and a context line with the board name and the PrikBoard icon. It sits in an attachment with the card's color as the side bar.
- **Board** and **Invite:** as in Discord.
- **No longer available:** as in Discord.

## Live previews and updates (phase 3)

`ChatActivity` already finds changed cards and the channels that follow them. For Slack:
- **Refreshing a preview** means calling `chat.unfurl` again on the remembered message. Whether Slack replaces an existing unfurl is phase 0, question 2. If it doesn't, Slack previews are snapshots, and the doc says so.
- **Updates** go out with `chat.postMessage` (one post per board after `UpdatesDelaySeconds`, the same lines as Discord).
- `/prikboard board` shows the snapshot as an ephemeral message.

## Save to PrikBoard and who is who (phase 4)

- **The shortcut.** *Save to PrikBoard* is a message shortcut. It sends a `trigger_id`, so the bot can open a **modal** right away (`views.open`). That is closer to how Linear and Jira do it than Discord's private replies. The modal has a board menu, a bucket menu and a title, filled in from `ChatSaves.TargetsAsync`.
- **Saving.** On `view_submission`, `ChatSaves.SaveAsync` creates the card as the person (`CardHistory.ActingAs`). The card gets the quoted message, with *From author in #channel, time*, a permalink back (`chat.getPermalink`), and up to four images. The bot then posts an ephemeral confirmation with the card's preview and reacts 📌.
- **Unknown person.** The modal shows *Connect your PrikBoard account*, which opens `#/connect/slack-account?code=…`. The page is `DiscordAccountPage` made generic. The link stores only the keyed hash of team + user id.
- **Sign in with Slack.** Someone who signs in to PrikBoard with Slack's OpenID Connect (a generic provider under `Auth:Providers`) is known by that sign-in, like a Discord sign-in today.

## Web

- `IntegrationsDialog` gets a *Slack* section next to *Discord*: workspace, channel picker (only channels where the bot is a member, no Slack Connect channels), *Whole workspace*, chips with 🔔, and channel settings. These are the same components with the platform passed in.
- `DiscordBotSettings` gets a sibling, `SlackAppSettings`, under Admin → *Instance*.
- Account gets a *Slack* section. The members list shows a Slack badge for the `slack` issuer.

## Phases

0. **Spike** (an evening, in a real workspace; see Testing, step 4). Answer these:
   1. Does `link_shared` give the URL with its `#/boards/…` part? If not, PrikBoard needs share links without a fragment, which is a design change before anything else.
   2. Does a second `chat.unfurl` on the same message replace the unfurl?
   3. Does the manifest prefill link work?
   4. Which unfurl domain works for local testing (an ngrok static domain)?
1. **Shared chat model** (built). A refactor and migration, with Discord unchanged and its tests green.
2. **Slack app setup, gateway, previews, Integrations UI**, plus the fake Slack.
3. **Live previews and updates.**
4. **Save to PrikBoard and account linking.**
5. **Later:** bare ids (opt-in `channels:history`), buttons on unfurls (Assign me, Move to…, Mark done), DMs for your own assignments, an App Home tab with your cards, a daily digest, "Add to Slack" for several workspaces (OAuth, a public events URL), Work Objects.

## Testing (without a running Slack)

Slack can't run locally: there is no self-hosted or open-source Slack that runs Slack apps. Mattermost and Rocket.Chat copy Slack's webhooks, not Socket Mode, unfurls or modals. Testing therefore has four layers, and only the last needs Slack.

1. **API tests, no Slack** (CI, `dotnet test PrikBoard.slnx`). `FakeSlackGateway` implements `ISlackGateway`, the same way `FakeDiscordGateway` stands in for Discord in `DiscordApiTests`. Tests raise `link_shared`, slash command and shortcut events on it, then check the unfurls, posts and modals it recorded. This covers:
   - connecting and disconnecting channels, and the admin app setup with the token checks faked;
   - every preview case, and the silence cases: another host, a board the workspace can't see, an unconnected channel, a Slack Connect channel, a used invite;
   - updates, saves as the person, and account linking.
2. **Fake Slack, everything local** (`tools/FakeSlack`, an ASP.NET minimal app on `http://localhost:5090`, a `fake-slack` entry in `.claude/launch.json`). It plays the parts of Slack the gateway uses:
   - `/api/{method}` answers the Web API calls above, from a seeded workspace with channels (one of them Slack Connect) and users (admins and not).
   - `/ws` is the Socket Mode socket. It sends envelopes and waits for acks, and it can drop the connection on demand to test reconnecting.
   - The page at `/` is a pretend Slack. You post a message as a chosen user, and its links on the unfurl domain produce `link_shared`, from the composer or after sending. You can also run `/prikboard board` and use *Save to PrikBoard* on a message. Unfurls, updates, ephemeral messages and modals show under the messages: Block Kit drawn roughly, with the raw JSON and an *Open in Block Kit Builder* link.

   Point PrikBoard at it with `Slack__ApiUrl=http://localhost:5090/api/`, then paste the fake's tokens under Admin → *Slack app*. From there the whole flow runs on your machine against the real `SlackGateway`: connect a channel under *Integrations*, paste a card link, change the card, save a message. `SlackGatewayTests` start FakeSlack with `WebApplicationFactory` so CI covers the socket and HTTP code too.
3. **See the messages.** Paste blocks JSON into Slack's Block Kit Builder (app.slack.com/block-kit-builder). It needs a free Slack account, not an app.
4. **Real Slack, free** (once for phase 0, and before release).
   - Create a free workspace (slack.com/get-started), or a free sandbox through the Slack Developer Program.
   - Give PrikBoard a public address for the unfurl domain: a free ngrok static domain pointing at Vite (`ngrok http --url=<name>.ngrok-free.app 5173`). Use it as the public address in Admin → *Instance*, so links open when clicked too. Sign-in through the tunnel needs the sign-in provider's callback for that host.
   - Create the app from the manifest, install it, and paste both tokens.
   - Connect a channel and try: a card link (sent, and typed in the composer), a board link, an invite, a board you didn't connect (nothing), another channel (nothing), a change to the card (does the unfurl refresh?), an update post, and Save to PrikBoard as a linked and an unlinked user.

   The fake can't answer phase 0's questions. It answers them the way we code it to. Only real Slack can.

## Open questions

1. **Fragments in `link_shared`** (phase 0). If Slack drops the `#` part, we need path-style share links (`/s/{shortId}/{number}`) that the web app redirects. That would also help other chat apps.
2. **Refreshing unfurls** (phase 0). Live previews depend on it.
3. **Bare ids.** Off, because it needs `channels:history`, which means reading every message in channels the bot is in. If we want it, make it a per-channel opt-in that an owner turns on, with the scope requested only then.
4. **Several workspaces.** Not with one Socket Mode app per instance. That needs an OAuth "Add to Slack" app with a public events URL, after there is a deployment (Discord's open question 10).
5. **Enterprise Grid.** An org-wide install gives events from many workspaces. The model keys on team id, so it should just work, but it is untested.
