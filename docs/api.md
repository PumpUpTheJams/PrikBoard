# PrikBoard API guide

This guide is for people and AI agents. The machine-readable contract is the OpenAPI 3.1 document at `/openapi/v1.json`. Point an agent framework or a client generator at it.

To explore and try the API in the browser, open the Scalar API reference at `/scalar` (in development http://localhost:5173/scalar, through the Vite proxy). It sends you through the default sign-in provider (GitHub) first, so "Test Request" runs with your session. To act as a machine instead, paste an access token into its Bearer field.

- **Base URL (local):** `http://localhost:5080`
- **Format:** JSON with camelCase properties. Enums such as `color` are strings.
- **Authentication:** every `/api` endpoint needs a caller, except `listAuthProviders` and `logout`, and the reads of a public board (getBoard, getBoardByShortId, getBoardAvatar, getImageContent, getDrawingContent), which answer anyone without credentials. Machines send an OAuth 2.0 access token: `Authorization: Bearer <token>`. The OpenAPI document and `/health` are public.
- **Access:** boards are members-only. A 404 means "not found, or you are not a member"; a 403 means your role (or token scope) is too low.

## Authentication

People sign in in the browser through an identity provider (GitHub by default, or Google, Microsoft, Auth0 and others) and then hold a session cookie. Machines and agents never go through that flow. They send a bearer token, one of two kinds:

- **A personal access token** (`pb_pat_...`): a person creates it under Account → *Agents and access tokens* and pastes it into the agent. This is the quickest way to let Claude Code, Cursor or a script work on your boards. See [Personal access tokens](#personal-access-tokens).
- **An OAuth access token** from a trusted identity provider, usually with the **Client Credentials** grant, for the audience `api://prikboard`. Owners add such clients to boards one by one (see below).

- `boards:read` allows `GET` requests, and `boards:write` allows everything.
- A token without the needed scope gets `403`. A missing, invalid, expired or untrusted token gets `401` with `WWW-Authenticate: Bearer`.
- Call `whoAmI` (`GET /api/me`) to check a token and see its scopes, issuer and client id.

## Members and roles

Every board has members, each with a role:

| Role | Can |
| --- | --- |
| **Viewer** | Read the board, its cards and images. |
| **Editor** | Also create, change and delete buckets, cards and images. |
| **Owner** | Also rename or delete the board, manage members and create invite links. |

- listBoards and getBoard include `yourRole`. Whoever creates a board becomes its owner, and a board always keeps at least one owner (changing or removing the last one gives `409`).
- A token's scope applies on top of its role: a `boards:read` token cannot write even as an Editor.
- Members are people (`kind: User`) and agents (`kind: Agent`). Nobody's email or SSO name is stored, so members show with the display name they chose themselves.

### Getting an agent onto a board

Every token client is an **agent**, identified by its issuer id and client id. A new agent sees no boards.

1. Call `whoAmI` with the token. It returns `principalKind: Agent`, your `id` and your `clientId`.
2. Ask a board owner to add you with `POST /api/boards/{boardId}/agents` with `{"issuer": "...", "clientId": "...", "role": "Editor"}`.
3. Optionally set your display name with `PATCH /api/me` (`{"displayName": "Release bot"}`).

Boards an agent creates itself belong to it. Agents cannot accept invite links or look people up by email; those are browser-only, so a link an agent comes across cannot get it onto a board.

### Invite links

Owners create single-use links with createBoardInvite. The response holds the token and a `path` (`/#/invite/{token}`) **once**; only a hash is stored. The link expires after 7 days by default (`expiresInDays`, 1 to 30), and owners can revoke it. The first signed-in person to accept it becomes a member with the link's role, or keeps their current role if that is higher. Tokens travel in POST bodies, never in query strings.

PrikBoard is invite-only: an open invite link is also the only way for a newcomer to get an account. Someone who opens it while signed out signs in with the link as the return URL, and only then is their user created ([Authentication](authentication.md#who-may-sign-in)).

### Admins

The instance's first user is its admin, and admins can make others admin. `whoAmI` returns `isAdmin`. The `admin*` operations below are for admins in the browser only: everyone else, and every token, gets `403`, and there are no MCP tools for them. Admins see who is on every board, not what is on it; to look inside a board, an admin adds themself, which its members see.

Setting up providers and issuers is described in [Authentication](authentication.md).

## Personal access tokens

A personal access token acts as **its own agent on your boards**: what it does shows as that agent (its name is the token's name), never as you.

- It can use every board you are a member of, or only the boards you chose when you created it.
- Its role on a board is the **lower** of your role there and the token's access: *Read only* (Viewer, scope `boards:read`) or *Can edit* (Editor, scopes `boards:read boards:write`).
  - Owner is never possible: tokens don't manage members, invites or tokens, and don't rename or delete boards.
  - If you lose access to a board, so do your tokens.
- Boards a token creates belong to you.
- Tokens expire (90 days by default, at most a year). You can revoke them at any time, which takes effect at once.
- Only a hash is stored. The token is shown once, when you create it.

```bash
curl -H "Authorization: Bearer pb_pat_..." http://localhost:5080/api/boards
```

The VS Code extension gets its own kind of token, `App`, by signing in through PrikBoard (approveAppSignIn, exchangeAppSignIn); it acts as you rather than as an agent. See *App sign-in* in [authentication.md](authentication.md).

A Discord server connected for link previews has a token of kind `Discord`: read-only, acting as the server's own agent, which is a Viewer member of the connected boards. It counts toward the tokens of the owner who connected it and shows in their list. See [discord.md](discord.md).

Tokens are managed with listAccessTokens, createAccessToken and revokeAccessToken, from a browser session only: a token can never create another token. Each token is limited to 600 requests a minute (`RateLimits:AccessTokenRequestsPerMinute`). One address may present at most 30 unknown tokens a minute (`RateLimits:UnknownAccessTokensPerMinute`); after that it gets 429 with `Retry-After` for every token not looked up before.

## MCP

PrikBoard is also an [MCP](https://modelcontextprotocol.io) server at **`/mcp`** (Streamable HTTP, stateless). It authenticates with a bearer token only, a personal access token or an issuer token, never the session cookie. Add it to Claude Code with:

```bash
claude mcp add --transport http prikboard http://localhost:5173/mcp --header "Authorization: Bearer pb_pat_..."
```

| Tool | What it does |
| --- | --- |
| `list_boards` | Your boards with your role on each |
| `get_board` | Compact overview: the board's agent `rules`, buckets in order with their cards (title, tags, assignees, checklist progress, creator), plus loose cards, the board's `tags` and the `people` who can be assigned |
| `get_board_rules` | Just the board's agent rules and when they last changed |
| `search_cards` | By bucket (name or id), `tags` (any of them), assignee (name or id) or text |
| `get_card` | Everything about one card, with its content as `components` |
| `create_card` | In a bucket by name or id (default: the first bucket). `assignees` are names or principal ids from `people`. `body`, `checklist` and `links` become its first Text, Checklist and Links components |
| `update_card` | Same PATCH rules as updateCard; `assignees` by name or id |
| `add_card_component`, `update_card_component`, `delete_card_component` | A card's content. A component is named by its id, its title, or its kind when the card has one component of that kind; `delete_card_component` is marked destructive |
| `add_image` | Like uploadImage, with the file base64-encoded in `data` (a `data:` URL works too): attached to a card (`cardId`) or on the board (`boardId`), in a free spot unless `x` and `y` are given. Slow beyond a few KB, since the agent writes out every character |
| `begin_image_upload` | Like createImageUpload, with the same targets as `add_image`: answers a one-time `uploadUrl` and a `curl -T` `command`, so an agent that can run commands sends the file from disk without it passing through the conversation |
| `move_card` | To a bucket by name or id; the server finds a free spot |
| `place_cards_in_bucket` | Several cards at once into a bucket's free spots, in order; it grows (`Down` unless `grow` says `Right`, `Left` or `Up`) when they don't fit |
| `set_working` | Assign yourself and show (or hide) the working halo, like setCardWorking |
| `delete_card`, `restore_card`, `list_deleted_cards`, `purge_card`, `empty_trash` | `delete_card` is marked destructive, so clients ask first; `restore_card` brings a deleted card back within 7 days, at its old place or at `x`/`y`; `list_deleted_cards` shows what is in the trash; `purge_card` and `empty_trash` (also destructive) delete from the trash for good |
| `collapse_bucket` | Folds a bucket up into a square on the board (its cards hidden there; `collapsed` in `get_board`), or opens it again |
| `bring_bucket_to_front` | Puts a bucket on top of the buckets it overlaps, for everyone: drawn above them, and cards put where they overlap join it |
| `create_bucket`, `rename_bucket` | Bucket housekeeping |
| `create_tag`, `update_tag`, `delete_tag`, `restore_tag` | The board's tags, by name or id. `create_tag` and `update_tag` take a `color`; renaming and deleting change every card. `delete_tag` is marked destructive, and `restore_tag` puts a deleted tag back on its cards |

The tools offer the same features as the REST endpoints. Where REST takes ids (`bucketId`, `assigneeIds`, a tag id), the tools also take names, resolved on the card's board.

The tools use the same access checks, validation and layout rules as the REST endpoints, and people watching the board see the changes live. There are no tools for members, invites, tokens or deleting boards. A request with a foreign `Origin` header gets `403`. Tool errors (no access, read-only token, an unknown bucket name) come back as tool results with `isError`, written for the model to act on.

**Agent rules.** A board can have rules in Markdown (`rules` in getBoard): what the board is about and how agents should handle its cards. Only owners set them, through updateBoard; the web app opens them from the tool rail. Agents treat card text as data, but the rules as instructions to follow in every action on the board. The server instructions say so, get_board shows the rules first, and get_board_rules returns them on their own. After every successful write on a board with rules, the tool result gets a second text component: a short reminder with the time the rules last changed, so an agent notices when they change and re-reads them. The rules themselves are not repeated.

## The board model

A board is a freeform canvas. It holds **buckets**, named rectangles such as *To do*, *Doing* and *Done*, and **cards**, which are 200×120 px cards. A card (or an image on the board) put somewhere joins the topmost bucket that contains its centre, and its `bucketId` says which one. It stays in that bucket while buckets move: a moved bucket carries exactly its own cards and images, also over or under another bucket, and takes nobody else's. A resized bucket lets go of its items now outside it and takes only loose ones it now covers; a new bucket takes what it is drawn around. A card outside every bucket is loose (`bucketId: null`). Buckets stack in creation order, with the newest on top.

Coordinates are canvas pixels. The board has no edge at the origin: x and y run from -100000 to 100000, so things can go left of and above 0,0 too. The server snaps them to a 20 px grid, and it works out each card's bucket again whenever a card or bucket moves.

A card's title may be empty: the web app then shows no title on the card, and a greyed suggestion in the expanded card's title field. Besides a title, a card has a `color` (fill), `tags`, `assignees` (people and agents), an `agentWorking` flag, who created it (`createdBy`, `createdById`; null for cards from before this was recorded) and its content as `components`. The board shows the title, tags and assignees on the card. Everything else appears when the card is expanded.

A card's content lives on its own small canvas, shown when the card is expanded. It holds any number of components: `Text` (free text), `Checklist` (`items`) and `Links` (titled `links`), each with an optional `title` and a place (`x`, `y`, `width`, `height` in card pixels, snapped to the 20 px grid). Images attached to the card sit on the same canvas. Change components one at a time with addCardComponent, updateCardComponent and deleteCardComponent, so people editing different components of a card never overwrite each other. A card's `path` is its address in the web app: opening `/#/boards/{shortId}/cards/{number}` shows the board with that card expanded.

**Readable ids.** Every board has a `shortId` (6 random characters, never changed) and a `key` (such as `MPCT`, shown before card numbers; owners change it with updateBoard, and it is not unique). Every card has a `number` on its board (1, 2, 3, …, never reused, not even after a delete) and a `readableId` such as `MPCT-415`. Paths use the short id and the number, so renaming a board or changing its key never breaks a link. getCardByNumber looks up the card a path points at. The older form `/#/boards/{boardId}/cards/{cardId}` still works: the web app opens it, links in that form to cards on the same board are stored as the card's path, and MCP tools take it. MCP tools also take a board's short id and a card's readable id, which they look up among the boards you can reach (when several of them have a card with that readable id, the tool asks for the path).

A board has its own list of **tags** (`tags` in getBoard, with how many cards use each). Cards pick their tags from it, and a new name on a card adds it to the list. A tag stays when no card uses it; renaming or deleting one changes every card on the board. Each tag has a `color`: `Default` shows it as a shade of the card's fill, while `Brick`, `Rust`, `Ochre`, `Moss`, `Pine`, `Teal`, `Ocean`, `Indigo`, `Violet`, `Berry` and `Ink` make it a solid chip of that color on every card, for things that should stand out, such as priority. **Assignees** are the board's `people`: its members, and the agents of members' personal access tokens that reach the board.

## Typical agent workflow

1. `GET /api/boards` (listBoards): find the project.
2. `GET /api/boards/{boardId}` (getBoard): get a full snapshot with every bucket and card.
3. Act on it:
   - `POST /api/boards/{boardId}/cards` (createCard) adds a task.
   - `POST /api/cards/{cardId}/move` (moveCard) with a `bucketId` moves a task, for example from *Doing* to *Done*. The server picks a free spot inside the bucket, so you never need coordinates.
   - `PATCH /api/cards/{cardId}` (updateCard) edits the title, fill, tags or assignees. Reuse the board's `tags`, and assign people by `principalId` from `people`.
   - `POST /api/cards/{cardId}/components` (addCardComponent) and `PATCH /api/cards/{cardId}/components/{componentId}` (updateCardComponent) add and edit its content: text, checklists and links.
4. When you start working on a card yourself, call `POST /api/cards/{cardId}/working` (setCardWorking) with `{"working":true}`: it assigns you and people see a lilac halo around the card. Send `false` when you are done; you stay assigned.

```bash
export TOKEN=...   # an access token from your identity provider (see docs/authentication.md)
alias pb='curl -H "Authorization: Bearer $TOKEN"'

# Create a board (starts with the buckets To do / Doing / Done)
pb -X POST localhost:5080/api/boards -H "Content-Type: application/json" \
  -d '{"name":"Website relaunch"}'

# Add a card (lands in the first bucket)
pb -X POST localhost:5080/api/boards/{boardId}/cards -H "Content-Type: application/json" \
  -d '{"title":"Pick a color palette","color":"Coral","tags":["design"]}'

# Claim it: assigns you (the token's agent) and shows the working halo
pb -X POST localhost:5080/api/cards/{cardId}/working -H "Content-Type: application/json" -d '{"working":true}'

# Assign a teammate (a principalId from people in getBoard)
pb -X PATCH localhost:5080/api/cards/{cardId} -H "Content-Type: application/json" \
  -d '{"assigneeIds":["{yourId}","{teammateId}"]}'

# Add a checklist, then tick off part of it (componentId from the response, or from components in getCard)
pb -X POST localhost:5080/api/cards/{cardId}/components -H "Content-Type: application/json" \
  -d '{"kind":"Checklist","title":"Done when","items":[{"text":"Three options"},{"text":"Team vote"}]}'
pb -X PATCH localhost:5080/api/cards/{cardId}/components/{componentId} -H "Content-Type: application/json" \
  -d '{"items":[{"text":"Three options","done":true},{"text":"Team vote","done":false}]}'

# Move it into another bucket
pb -X POST localhost:5080/api/cards/{cardId}/move -H "Content-Type: application/json" \
  -d '{"bucketId":"{doneBucketId}"}'

# Or put it at exact coordinates (its bucket follows from where it lands)
pb -X POST localhost:5080/api/cards/{cardId}/move -H "Content-Type: application/json" \
  -d '{"x":1400,"y":80}'
```

## Endpoints

| operationId | Method and path | Purpose |
| --- | --- | --- |
| listBoards | `GET /api/boards` | All boards, newest first, without buckets and cards |
| createBoard | `POST /api/boards` | New board with the default buckets side by side |
| getBoard | `GET /api/boards/{boardId}` | Full snapshot: agent `rules` and `rulesUpdatedAt`, buckets in list order (oldest first; `stackedAt` says how they stack), cards grouped by bucket, images, drawings (pen strokes, without their points), the board's `tags` and the `people` who can be assigned. A public board answers anyone who isn't a member too (not tokens), signed in or not, with `visiting: true`, no `people`, and assignees, creators and who added images as anonymous (empty) unless it shows people (`publicShowsPeople`) |
| getBoardByShortId | `GET /api/boards/{shortId}` | The same, by the short id in the board's address |
| getBoardAvatar | `GET /api/boards/{boardId}/avatars/{principalId}` | The picture of someone on the board (member, creator or assignee), for visitors of a public board that shows people: their `avatarUrl` points here |
| updateBoard | `PATCH /api/boards/{boardId}` | Rename, change the `key` (a letter, then letters and digits, 2 to 10 in all; upper-cased), edit the description or agent `rules` (Markdown, at most 20000 characters; an empty string clears them), or make the board public (`isPublic`) and show its people to visitors (`publicShowsPeople`). Owners only |
| deleteBoard | `DELETE /api/boards/{boardId}` | Delete the board with all buckets and cards |
| exportBoard | `GET /api/boards/{boardId}/export` | The board as one downloadable JSON file (`format: "prikboard-board"`, `version: 5`, `appVersion`: the PrikBoard version that wrote it): name, description, agent rules, buckets, tags (and `tagColors` for tags with a color), cards with their components, and images (bytes as base64; attached ones keep their place on the card's canvas). No members, roles, user ids or assignees. Ids in the file are local to it |
| importBoard | `POST /api/boards/import` | Send an export file as the body. Always makes a new board that you own (`201`), with new ids; positions snap to the grid and each card's bucket follows from where it sits. Cards are created by you. `400` for a file that is not valid, newer than the server reads (a newer `version`, or fields it doesn't know: the message names them and the file's `appVersion`), or from before notes were renamed cards (versions 1 and 2: export the board again); `403` for tokens limited to chosen boards. Body at most 64 MB |
| createBucket | `POST /api/boards/{boardId}/buckets` | Draw a bucket at a rectangle, or to the right of the others. Loose cards it covers join it |
| updateBucket | `PATCH /api/buckets/{bucketId}` | Rename, recolor, move or resize. Moving carries the bucket's own cards, images and drawings along (or exactly the ones in `carryCardIds`, `carryImageIds` and `carryDrawingIds`, which undo uses) and changes no item's bucket, also over another bucket; a new `x`/`y` together with a new size is a resize from a top or left corner or edge: the cards and images stay put, ones now outside leave it, loose ones now inside join it. `collapsed: true` folds it into a 120 px square in its top-right corner, hiding its cards, images and drawings (they keep their places, and no other items join it) until `collapsed: false`; a collapsed bucket moves but can't be resized. Moving, resizing and opening put it on top of the other buckets (`stackedAt`) |
| bringBucketToFront | `POST /api/buckets/{bucketId}/front` | Put the bucket on top of the others, for everyone: where buckets overlap it is drawn above them and items put there join it. No item changes bucket |
| placeCardsInBucket | `POST /api/buckets/{bucketId}/cards` | Put several cards (`cardIds`, in order) into the bucket's free slots, row by row around the cards and images in it (also a collapsed bucket). When they don't fit it grows towards `grow`: `Down` (default), `Right`, `Left` or `Up`. The bucket goes on top of the others. Returns the bucket and the cards |
| deleteBucket | `DELETE /api/buckets/{bucketId}` | Delete a bucket. Its cards stay where they are and become loose |
| restoreBucket | `POST /api/buckets/{bucketId}/restore` | Bring back a deleted bucket with the same id and place in the stack; the cards it covers join it again |
| listCards | `GET /api/boards/{boardId}/cards?bucketId=&tag=&assignee=&search=` | Filter by bucket, tags (repeat `tag`; any of them matches), assignee (principal id or current name), or search the title and content: text, checklist items and links (all case-insensitive) |
| createCard | `POST /api/boards/{boardId}/cards` | New card in a bucket's free spot (default: the first bucket), or at `x`/`y`. Butter by default. Can include tags, `assigneeIds` and `components` (laid out automatically without `x`/`y`). Records you as its creator |
| getCard | `GET /api/cards/{cardId}` | A single card |
| getCardByNumber | `GET /api/boards/{shortId}/cards/{number}` | The card a path points at, by its board's short id and its number |
| updateCard | `PATCH /api/cards/{cardId}` | Edit the title, fill, tags, `assigneeIds` or `agentWorking` |
| setCardWorking | `POST /api/cards/{cardId}/working` | `{"working":true}` assigns you and shows the halo; `false` takes the halo away (you stay assigned) |
| moveCard | `POST /api/cards/{cardId}/move` | Move into a bucket (`bucketId`) or to coordinates (`x` and `y`), or both: there, in that bucket |
| deleteCard | `DELETE /api/cards/{cardId}` | Delete a card (and the images attached to it) |
| restoreCard | `POST /api/cards/{cardId}/restore` | Bring back a deleted card with the same id, content, creator and place, and the images deleted along with it; with `{"x","y"}` it goes there instead |
| listCardHistory | `GET /api/cards/{cardId}/history?limit=` | Who changed what on a card and when, newest first (title, color, tags, assignees, agent flag, bucket moves, components, images, drawings, delete/restore); quick edits by one caller merged; also for deleted cards; members only |
| listDeletedCards | `GET /api/boards/{boardId}/trash?search=` | The trash: cards deleted in the last 7 days (with `deletedAt`), most recently deleted first, optionally searched, with the images deleted along with them |
| purgeCard | `DELETE /api/boards/{boardId}/trash/{cardId}` | Delete a card in the trash for good, with the images and drawings deleted along with it; `409` when it is not deleted |
| emptyTrash | `DELETE /api/boards/{boardId}/trash` | Delete every card in the board's trash for good; returns `{"purged"}`, how many cards went |
| addCardComponent | `POST /api/cards/{cardId}/components` | Add a `Text`, `Checklist` or `Links` component to the card's content, in a free spot unless `x`/`y` are given (at most 30 components). `id` puts back a removed component with the id it had (`409` when the card has it) |
| updateCardComponent | `PATCH /api/cards/{cardId}/components/{componentId}` | Edit a component's `title`, content (`text`, `items` or `links`, whichever fits its kind), position or size |
| deleteCardComponent | `DELETE /api/cards/{cardId}/components/{componentId}` | Remove a component |
| listBoardTags | `GET /api/boards/{boardId}/tags` | The board's tags by name, with how many cards use each |
| createBoardTag | `POST /api/boards/{boardId}/tags` | Add a tag, optionally with a `color`, without putting it on a card (`409` when it exists or the board has 100) |
| updateBoardTag | `PATCH /api/boards/{boardId}/tags/{tagId}` | Rename a tag (on the board and on every card; `409` when the new name is taken) and/or change its `color` |
| deleteBoardTag | `DELETE /api/boards/{boardId}/tags/{tagId}` | Delete a tag from the board and from every card |
| restoreBoardTag | `POST /api/boards/{boardId}/tags/{tagId}/restore` | Bring back a deleted tag and put it back (last) on the cards it was removed from (`409` when a tag has that name again) |
| uploadImage | `POST /api/boards/{boardId}/images?fileName=&cardId=&x=&y=&width=&caption=` | Add an image or GIF: the file itself is the request body, with its content type. On the board at `x`/`y` (default: right of everything), or attached to a card with `cardId`, at `x`/`y` on its canvas (default: a free spot) |
| createImageUpload | `POST /api/boards/{boardId}/image-uploads` | A one-time `uploadUrl` (and a `curl -T` `command`) for one image, placed as the body says (`cardId`, `x`, `y`, `width`, `caption`, `fileName`) and added by you. Expires after 15 minutes; at most 20 unused per caller (`409`) |
| completeImageUpload | `PUT /api/image-uploads/{token}` | Send the file to an `uploadUrl`: the raw bytes as the body, no authentication (the URL is the key). `201` with the image; `404` when the URL is unknown, used or expired, or its creator lost Editor access (or revoked the token they used). After a `413` or `415` the URL still works |
| getImageContent | `GET /api/images/{imageId}/content` | Download the image file: inline, under its own `fileName`. Cacheable for good (ETag, `304` on `If-None-Match`), with byte ranges |
| updateImage | `PATCH /api/images/{imageId}` | Move (`x`, `y`), resize (`width`) or re-caption an image; attach it to a card (`cardId`) or put it back on the board (`detach: true`) |
| deleteImage | `DELETE /api/images/{imageId}` | Delete an image |
| restoreImage | `POST /api/images/{imageId}/restore` | Bring back a deleted image (`409` while the card it was attached to is deleted: restore the card) |
| createDrawing | `POST /api/boards/{boardId}/drawings` | Start a drawing with one pen stroke: `x`/`y` (snapped to the grid) and `points` `[x, y, pressure]` relative to them, with `color` (an ink, default `Chalk`) and `strokeWidth` (2, 4 or 8). On the board, or on a card's canvas with `cardId`. On the board it joins the bucket containing its centre. A drawing has one ink per stroke (`colors`). `409` past 5000 drawings on a board |
| getDrawingContent | `GET /api/drawings/{drawingId}/content?v=` | The drawing's `strokes`, each with its `strokeWidth` and `points` (`x`/`y` to 0.1 px, pressure to 0.01). Fetch the drawing's `contentUrl`, which names the revision and changes with the strokes, so it is cacheable for good |
| addDrawingStroke | `POST /api/drawings/{drawingId}/strokes` | Add a stroke to a drawing (`x`/`y` of the stroke on the drawing's canvas, `points`, `color`, `strokeWidth`, as in createDrawing); the drawing grows to hold it. The web app's pen adds every stroke after the first to the same drawing until it is put down. `409` past 1000 strokes, 50000 points or 10000 px |
| deleteDrawingStroke | `DELETE /api/drawings/{drawingId}/strokes/{index}` | Take a stroke off (0 is the first drawn) and shrink the drawing to the rest; `409` for its only stroke (delete the drawing) |
| transformDrawing | `POST /api/drawings/{drawingId}/transform` | Scale (`scaleX`, `scaleY`, 0.05 to 20) and then turn clockwise (`rotation`, degrees) about a pivot (`pivotX`/`pivotY` on its canvas, both or neither; default the middle of its box). The points change and the box follows them (corner on the grid, half the thickest line clear); line widths stay. The web app's corner handles scale (Shift stretches) and the round handle above turns it (Shift: 15° steps). `409` past 10000 px or off the board |
| setDrawingStrokes | `PUT /api/drawings/{drawingId}/strokes` | New `x`/`y` and the points of every stroke (`strokes`, as many as it has, each `strokeWidth` and `points`; inks stay). Undo uses it to put a transformed drawing back exactly |
| updateDrawing | `PATCH /api/drawings/{drawingId}` | Move (`x`, `y`) or recolor a drawing (`color` for every stroke, or `colors` with one ink per stroke); put it on a card (`cardId`) or back on the board (`detach: true`), with both `x` and `y` there |
| deleteDrawing | `DELETE /api/drawings/{drawingId}` | Delete a drawing |
| restoreDrawing | `POST /api/drawings/{drawingId}/restore` | Bring back a deleted drawing (`409` while its card is deleted: restore the card) |
| listAuthProviders | `GET /api/auth/providers` | Sign-in providers for the browser, the default first (public) |
| logout | `POST /api/auth/logout` | End the browser session (public) |
| listTokenIssuers | `GET /api/auth/token-issuers` | Ids of the configured token issuers, for adding agents |
| listAccessTokens | `GET /api/me/tokens` | Your personal access tokens (never the tokens themselves). Sessions only |
| createAccessToken | `POST /api/me/tokens` | New personal access token: `name`, `access` (Viewer or Editor), optional `boardIds` and `expiresInDays`. The token is in the response once. Sessions only |
| revokeAccessToken | `DELETE /api/me/tokens/{tokenId}` | Revoke a token at once. Sessions only |
| approveAppSignIn | `POST /api/me/app-sign-ins` | Allow an app (the VS Code extension) to sign in as you: `name`, `codeChallenge` (PKCE S256), optional loopback `redirectUri`. Returns a one-time `code` (5 minutes). Sessions from PrikBoard's own pages only; at most 5 waiting |
| exchangeAppSignIn | `POST /api/auth/app-sign-ins/token` | The app trades `code` and `codeVerifier` for a token of kind `App` that acts as the person, for 90 days. Anonymous, rate-limited; each code works once |
| getVersion | `GET /api/auth/version` | Version of the running server |
| whoAmI | `GET /api/me` | Who you are: principal id, display name, linked sign-ins (sessions) or issuer and client id (tokens), and scopes |
| updateMe | `PATCH /api/me` | Set your display name (`""` clears it) |
| useSignInAvatar | `PUT /api/me/avatar` | Take the picture of the provider you signed in with (`suggestedAvatarUrl`; GitHub only) as your avatar again after removing it; GitHub sign-ins otherwise do this themselves. `409` without one, `502` when it can't be fetched. Sessions only |
| removeAvatar | `DELETE /api/me/avatar` | Remove your avatar, so your initials show again, and keep later sign-ins from taking it (`avatarOptedOut`). Sessions only |
| getAvatar | `GET /api/avatars/{principalId}` | A person's avatar picture: the `avatarUrl` on people in other answers (versioned with `v`, so cacheable) |
| unlinkIdentity | `DELETE /api/me/identities/{identityId}` | Remove a linked sign-in (not the last one). Sessions only |
| listBoardMembers | `GET /api/boards/{boardId}/members` | People and agents on the board, with their roles (Viewer) |
| addBoardMember | `POST /api/boards/{boardId}/members` | Add a person by `principalId` with a role (Owner) |
| addBoardAgent | `POST /api/boards/{boardId}/agents` | Add an agent by `issuer` and `clientId` with a role (Owner) |
| updateBoardMember | `PATCH /api/boards/{boardId}/members/{principalId}` | Change a member's role (Owner) |
| removeBoardMember | `DELETE /api/boards/{boardId}/members/{principalId}` | Remove a member (Owner), or leave the board yourself |
| findUserByEmail | `POST /api/boards/{boardId}/members/lookup` | Find people by their exact, verified email (Owner, sessions only, rate-limited) |
| listBoardInvites | `GET /api/boards/{boardId}/invites` | Pending invite links (Owner) |
| createBoardInvite | `POST /api/boards/{boardId}/invites` | New single-use invite link with a role (Owner) |
| revokeBoardInvite | `DELETE /api/boards/{boardId}/invites/{inviteId}` | Revoke an invite link (Owner) |
| previewInvite | `POST /api/invites/preview` | Board name and role behind an invite token (sessions only) |
| acceptInvite | `POST /api/invites/accept` | Spend an invite token and join the board (sessions only) |
| approveDiscordConnect | `POST /api/me/discord-connects` | Connect a Discord server to boards you own: `codeChallenge` (PKCE S256, from the bot's link) and `boards` (`boardId`, `wholeServer`). Returns a one-time `code` (5 minutes) for `/prikboard finish`. Sessions from PrikBoard's own pages only; Owner on every board; at most 5 waiting |
| exchangeDiscordConnect | `POST /api/discord/connect/token` | The Discord bot trades `code` and `codeVerifier`, with `guildId`, `guildName`, `channelId` and `channelName` from Discord's interaction, for the server's read-only token of kind `Discord` (365 days, replacing its old one). The server becomes a Viewer on the boards. Anonymous, rate-limited; each code works once |
| getDiscordStatus | `GET /api/discord/status` | The connected server's boards and channel limits, and when its token ends (Discord tokens only) |
| getLinkPreview | `POST /api/link-previews` | What a pasted `url` points to, for the bot: a `Card`, `Board` or open `Invite` (never spent). Anything else, including a `channelId` the board isn't previewed in, is the same `404` (Discord tokens only, rate-limited per server) |
| revokeDiscordToken | `DELETE /api/discord/token` | The bot revokes its own server token, for `/prikboard disconnect` (Discord tokens only) |
| listDiscordConnections | `GET /api/boards/{boardId}/discord` | Discord servers that preview the board's links, with their channels or `null` for the whole server (Owner) |
| removeDiscordConnection | `DELETE /api/boards/{boardId}/discord/{guildId}` | Take a Discord server off the board (Owner) |
| removeDiscordChannel | `DELETE /api/boards/{boardId}/discord/{guildId}/channels/{channelId}` | Stop previewing the board in one channel; the last one disconnects the server (Owner) |
| adminListBoards | `GET /api/admin/boards` | Every board with its members and their roles, newest first (admins, sessions only) |
| adminDeleteBoard | `DELETE /api/admin/boards/{boardId}` | Delete any board with everything on it (admins, sessions only) |
| adminListUsers | `GET /api/admin/users` | Every user, with admin flag, board count and the boards they own alone (admins, sessions only) |
| adminSetBoardMember | `PUT /api/admin/boards/{boardId}/members/{userId}` | Add a person to any board with a role, or change their role (admins, sessions only) |
| adminRemoveBoardMember | `DELETE /api/admin/boards/{boardId}/members/{principalId}` | Take anyone off any board; the last owner stays (admins, sessions only) |
| adminUpdateUser | `PATCH /api/admin/users/{userId}` | Make a user an admin or take it away; one admin always stays (admins, sessions only) |
| adminDeleteUser | `DELETE /api/admin/users/{userId}` | Delete a user who owns no board alone; not yourself (admins, sessions only) |

Board, bucket, card and image endpoints need the **Viewer** role to read and **Editor** to change anything; updateBoard and deleteBoard need **Owner**.

The browser starts sign-in by navigating to `GET /api/auth/login/{provider}?returnUrl=/...`. It is left out of the OpenAPI document because it is a redirect, not an API call, and so is the form post `POST /api/auth/link/{provider}` that connects another sign-in to the signed-in user. The provider redirects back to `/api/auth/callback/{provider}`.

`GET /health` returns `Healthy` when the API is up.

## Live updates

Instead of polling getBoard, a client can follow a board live over **SignalR** at `/api/live` (WebSockets, falling back to server-sent events or long polling). It authenticates like the rest of the API: a session cookie from PrikBoard's own pages, or `Authorization: Bearer <token>` with `boards:read` or `boards:write`. Changes still go through the REST endpoints; the hub only tells you about them. Official SignalR clients exist for JavaScript, .NET, Java and Swift.

| Hub method | Who | What |
| --- | --- | --- |
| `Join(boardId)` | Members | Follow one board (and stop following the previous one). Returns `{boardId, role, key, viewers}`; fails for boards you are not a member of |
| `Leave()` | Anyone | Stop following the board |
| `Drag({kind, id, dx, dy, handle, items})` | Editors (tokens need `boards:write`) | Show others a drag in progress: `kind` is `Card`, `Bucket`, `Resize`, `Image`, `ImageResize` or `Group`, `dx`/`dy` the pointer travel in canvas pixels, and for resizes `handle` the corner or edge dragged (`BottomRight`, the default, `BottomLeft`, `TopRight`, `TopLeft`, `Top`, `Right`, `Bottom` or `Left`; an edge resizes in its direction only). `Group` moves several selected items together: `items` lists them all (1 to 1000 cards, buckets, images and drawings, `id` the one held, not what a listed bucket carries); other kinds send no `items`. Send at most every 50 ms |
| `DragEnd()` | Editors | The drag is over |

| Server message | Meaning |
| --- | --- |
| `BoardChanged({boardId, by})` | Something on the joined board was saved; call getBoard |
| `Presence({boardId, viewers})` | Who is on the board now (`principalId`, `kind`, `displayName`), each person once |
| `Dragging({key, principalId, displayName, kind, id, dx, dy, handle, items})` | Someone else's drag in progress |
| `DragEnded(key)` | They let go. Keep the item where it was until the next getBoard shows where it landed |
| `BoardsChanged()` | Your list of boards changed (renamed, deleted, you were added or removed); call listBoards |
| `AccessChanged(boardId)` | Your role on that board changed or you lost access: call `Join` again (it fails when access is gone) |

- Changes you make yourself are announced to you too, unless you send your connection id in an `X-PrikBoard-Live` header with the change (the web app does, since it reloads after its own changes anyway).
- Being removed from a board stops its messages at once.
- Live updates are a convenience: reconnect when the connection drops, and call getBoard after reconnecting, because changes made in between are not replayed.

## Images

Images and GIFs live on the board or are attached to a card (`cardId`), and getBoard lists them all under `images`. An attached image's `x` and `y` are on the card's own canvas, next to its components. updateImage moves an image between the board and a card: `cardId` attaches it, `detach: true` puts it back on the board, each at `x`/`y` there or in a free spot. Upload the raw file as the request body, not as a form:

```bash
pb -X POST "localhost:5080/api/boards/{boardId}/images?fileName=ingest-errors.png&cardId={cardId}"   -H "Content-Type: image/png" --data-binary @ingest-errors.png
```

Agents that can run commands but have no token in their shell (an MCP client) get a one-time URL instead, with createImageUpload or the `begin_image_upload` tool, and send the file there; the URL needs no authentication, so nothing secret goes into the command:

```bash
curl -sS -T ingest-errors.png "https://prikboard.example/api/image-uploads/pb_upl_..."
```

- **Formats.** PNG, JPEG, GIF and WebP up to 10 MB and 20000 pixels on a side. The server detects the format and pixel size from the file itself; SVG is refused because it can carry script.
- **Size.** `width` is the display width in canvas pixels, snapped to the grid and between 80 and 4000. The height always follows the aspect ratio. Without a width, an image keeps its own width up to 360.
- **Download** images from `contentUrl` with the same authentication as the rest of the API. The content of an image never changes, so it can be cached for good; revalidating with its `ETag` (`If-None-Match`) or `Last-Modified` answers `304`, and `Range` requests are served. The file comes with `Content-Disposition: inline` and its `fileName`, so saving it keeps the name.

## Conventions

- **Placement.** createCard and moveCard take a `bucketId`, both `x` and `y`, or all three. With a `bucketId` alone, the card goes to the first free grid slot inside that bucket. A full bucket grows downwards to make room. placeCardsInBucket does the same for several cards at once, and can grow the bucket to any side. With `x` and `y` alone, the card joins the topmost bucket containing its centre. With all three, it joins that bucket, which must contain its centre (400 otherwise): the web app names the bucket it shows in front where buckets overlap.
- **Ordering.** getBoard and listCards return cards bucket by bucket, in the order of `buckets`, with loose cards last. Each group runs top to bottom, then left to right. Clients draw cards in `updatedAt` order, so the most recently moved or edited card is on top.
- **Sizes.** Buckets are at least 240×160 px, room for one card with a gap around it. Smaller sizes are raised to the minimum after snapping.
- **PATCH** only changes the fields you send. Sending `""` for an optional text field (`description`, a component's `title` or `text`) clears it. A list you send (`tags`, `assigneeIds`, a component's `items` or `links`) replaces the whole list, and `[]` clears it. To tick off one checklist item, send the component's full `items` with that item changed.
- **Tags** are trimmed, lower-cased and hyphenated (`"Back end"` becomes `"back-end"`), at most 10 per card and 30 characters each. Cards store the names of the board's tags; a new name joins the board's list, which holds at most 100.
- **Assignees** are sent as `assigneeIds` (principal ids from `people` in getBoard) and come back as `{principalId, name, kind}` with the current display name; `kind` is `Person` or `Agent`. People show as initials, agents as a sparkle. At most 10 per card. Only people who can reach the board can be added; someone who loses access stays on the cards they had until they are taken off.
- **Links** need an absolute `http` or `https` URL, or another card's `path` (`/#/boards/{boardId}/cards/{cardId}`, in every card) to link that card. The path has no host, so the link works wherever PrikBoard runs; the web app opens it in place and shows the linked card's current title.
- **Colors.** Card fills are `Butter` (yellow), `Mint` (green), `Sky` (blue), `Coral` (salmon red) and `Lilac` (purple). Buckets also have a `color` (`Blue`, `Amber`, `Violet`, `Teal` or `Rose`); the web UI doesn't show it at the moment.
- **Deleting and restoring.** Deleting a card, bucket, image or tag hides it at once, but it can be restored for 7 days with the same id (restoreCard, restoreBucket, restoreImage, restoreBoardTag; the web app's undo uses these). After that it is gone for good. Restoring something that is not deleted is a `409`; restoring something that never existed or is gone for good is a `404`. Deleted things can't be changed (`404`). Boards are deleted for good straight away. Deleted cards are listed by listDeletedCards (the web app's trash window), and purgeCard or emptyTrash delete them for good before the 7 days are up.
- **Errors** use `application/problem+json`. Validation errors (400) list the offending fields under `errors`, for example a `bucketId` that is not on the card's board. A 401 means the request is not signed in or the token is invalid. A 403 means your role or the token's scope is too low. A 404 means the resource does not exist or you are not a member of its board. A 429 means you hit a rate limit (email lookups and invite links).
