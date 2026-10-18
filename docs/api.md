# PrikBoard API guide

This guide is for people and AI agents. The machine-readable contract is the OpenAPI 3.1 document at `/openapi/v1.json`. Point an agent framework or a client generator at it.

To explore and try the API in the browser, open the Scalar API reference at `/scalar` (in development http://localhost:5173/scalar, through the Vite proxy). It sends you through the default sign-in provider (GitHub) first, so "Test Request" runs with your session. To act as a machine instead, paste an access token into its Bearer field.

- **Base URL (local):** `http://localhost:5080`
- **Format:** JSON with camelCase properties. Enums such as `color` are strings.
- **Authentication:** every `/api` endpoint needs a caller, except `listAuthProviders` and `logout`. Machines send an OAuth 2.0 access token: `Authorization: Bearer <token>`. The OpenAPI document and `/health` are public.
- **Access:** boards are members-only. A 404 means "not found, or you are not a member"; a 403 means your role (or token scope) is too low.

## Authentication

People sign in in the browser through an identity provider (GitHub by default, or Google, Microsoft, Auth0 and others) and then hold a session cookie. Machines and agents never go through that flow. They get an access token from a trusted identity provider, usually with the **Client Credentials** grant, for the audience `api://prikboard`.

- `boards:read` allows `GET` requests, and `boards:write` allows everything.
- A token without the needed scope gets `403`. A missing, invalid, expired or untrusted token gets `401` with `WWW-Authenticate: Bearer`.
- Call `whoAmI` (`GET /api/me`) to check a token and see its scopes, issuer and client id.

## Members and roles

Every board has members, each with a role:

| Role | Can |
| --- | --- |
| **Viewer** | Read the board, its notes and images. |
| **Editor** | Also create, change and delete buckets, notes and images. |
| **Owner** | Also rename or delete the board, manage members and create invite links. |

- listBoards and getBoard include `yourRole`. Whoever creates a board becomes its owner, and a board always keeps at least one owner (changing or removing the last one gives `409`).
- A token's scope applies on top of its role: a `boards:read` token cannot write even as an Editor.
- Members are people (`kind: User`) and agents (`kind: Agent`). Nobody's email or SSO name is stored, so members show with the display name they chose themselves.

### Getting an agent onto a board

Every token client is an **agent**, identified by its issuer id and client id. A new agent sees no boards.

1. Call `whoAmI` with the token. It returns `principalKind: Agent`, your `id` and your `clientId`.
2. Ask a board owner to add you: *Share → Add an agent* in the browser, or `POST /api/boards/{boardId}/agents` with `{"issuer": "...", "clientId": "...", "role": "Editor"}`.
3. Optionally set your display name with `PATCH /api/me` (`{"displayName": "Release bot"}`).

Boards an agent creates itself belong to it. Agents cannot accept invite links or look people up by email; those are browser-only, so a link an agent comes across cannot get it onto a board.

### Invite links

Owners create single-use links with createBoardInvite. The response holds the token and a `path` (`/#/invite/{token}`) **once**; only a hash is stored. The link expires after 7 days by default (`expiresInDays`, 1 to 30), and owners can revoke it. The first signed-in person to accept it becomes a member with the link's role, or keeps their current role if that is higher. Tokens travel in POST bodies, never in query strings.

Setting up providers and issuers is described in [Authentication](authentication.md).

## The board model

A board is a freeform canvas. It holds **buckets**, named rectangles such as *To do*, *Doing* and *Done*, and **notes**, which are 200×120 px post-its. A note belongs to the topmost bucket that contains its centre, and its `bucketId` says which one. A note outside every bucket is loose (`bucketId: null`). Buckets stack in creation order, with the newest on top.

Coordinates are canvas pixels from the top-left corner. The server snaps them to a 20 px grid, and it works out each note's bucket again whenever a note or bucket moves.

Besides a title, a note has an optional `body`, a `color` (fill), `tags`, `assignees` (people and agents), a `checklist`, `links`, and an `agentWorking` flag. The board shows the title, tags and assignees on the post-it. Everything else appears when the post-it is expanded.

## Typical agent workflow

1. `GET /api/boards` (listBoards): find the project.
2. `GET /api/boards/{boardId}` (getBoard): get a full snapshot with every bucket and note.
3. Act on it:
   - `POST /api/boards/{boardId}/notes` (createNote) adds a task.
   - `POST /api/notes/{noteId}/move` (moveNote) with a `bucketId` moves a task, for example from *Doing* to *Done*. The server picks a free spot inside the bucket, so you never need coordinates.
   - `PATCH /api/notes/{noteId}` (updateNote) edits the title, body, fill, tags, assignees, checklist or links.
4. When you start working on a note yourself, assign yourself as an agent and set `agentWorking: true`. People see a lilac halo around the post-it until you set it back to `false`.

```bash
export TOKEN=...   # an access token from your identity provider (see docs/authentication.md)
alias pb='curl -H "Authorization: Bearer $TOKEN"'

# Create a board (starts with the buckets To do / Doing / Done)
pb -X POST localhost:5080/api/boards -H "Content-Type: application/json" \
  -d '{"name":"Website relaunch"}'

# Add a note (lands in the first bucket)
pb -X POST localhost:5080/api/boards/{boardId}/notes -H "Content-Type: application/json" \
  -d '{"title":"Pick a colour palette","color":"Coral","tags":["design"]}'

# Claim it as an agent and tick off part of the checklist
pb -X PATCH localhost:5080/api/notes/{noteId} -H "Content-Type: application/json" \
  -d '{"assignees":[{"name":"Claude","kind":"Agent"}],"agentWorking":true,
       "checklist":[{"text":"Three options","done":true},{"text":"Team vote","done":false}]}'

# Move it into another bucket
pb -X POST localhost:5080/api/notes/{noteId}/move -H "Content-Type: application/json" \
  -d '{"bucketId":"{doneBucketId}"}'

# Or put it at exact coordinates (its bucket follows from where it lands)
pb -X POST localhost:5080/api/notes/{noteId}/move -H "Content-Type: application/json" \
  -d '{"x":1400,"y":80}'
```

## Endpoints

| operationId | Method and path | Purpose |
| --- | --- | --- |
| listBoards | `GET /api/boards` | All boards, newest first, without buckets and notes |
| createBoard | `POST /api/boards` | New board with the default buckets side by side |
| getBoard | `GET /api/boards/{boardId}` | Full snapshot: buckets back to front, notes grouped by bucket |
| updateBoard | `PATCH /api/boards/{boardId}` | Rename or edit the description |
| deleteBoard | `DELETE /api/boards/{boardId}` | Delete the board with all buckets and notes |
| createBucket | `POST /api/boards/{boardId}/buckets` | Draw a bucket at a rectangle, or to the right of the others. Loose notes it covers join it |
| updateBucket | `PATCH /api/buckets/{bucketId}` | Rename, recolour, move or resize. Moving carries the bucket's notes along |
| deleteBucket | `DELETE /api/buckets/{bucketId}` | Delete a bucket. Its notes stay where they are and become loose |
| listNotes | `GET /api/boards/{boardId}/notes?bucketId=&tag=&assignee=&search=` | Filter by bucket, tag or assignee name, or search title and body (all case-insensitive) |
| createNote | `POST /api/boards/{boardId}/notes` | New note in a bucket's free spot (default: the first bucket), or at `x`/`y`. Butter by default. Can include tags, assignees, checklist and links |
| getNote | `GET /api/notes/{noteId}` | A single note |
| updateNote | `PATCH /api/notes/{noteId}` | Edit the title, body, fill, tags, assignees, checklist, links or `agentWorking` |
| moveNote | `POST /api/notes/{noteId}/move` | Move into a bucket (`bucketId`) or to coordinates (`x` and `y`) |
| deleteNote | `DELETE /api/notes/{noteId}` | Delete a note (and the images attached to it) |
| uploadImage | `POST /api/boards/{boardId}/images?fileName=&noteId=&x=&y=&width=&caption=` | Add an image or GIF: the file itself is the request body, with its content type. On the canvas at `x`/`y` (default: right of everything), or attached to a note with `noteId` |
| getImageContent | `GET /api/images/{imageId}/content` | Download the image file |
| updateImage | `PATCH /api/images/{imageId}` | Move (`x`, `y`), resize (`width`) or re-caption an image |
| deleteImage | `DELETE /api/images/{imageId}` | Delete an image |
| listAuthProviders | `GET /api/auth/providers` | Sign-in providers for the browser, the default first (public) |
| logout | `POST /api/auth/logout` | End the browser session (public) |
| listTokenIssuers | `GET /api/auth/token-issuers` | Ids of the configured token issuers, for adding agents |
| whoAmI | `GET /api/me` | Who you are: principal id, display name, linked sign-ins (sessions) or issuer and client id (tokens), and scopes |
| updateMe | `PATCH /api/me` | Set your display name (`""` clears it) |
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

Board, bucket, note and image endpoints need the **Viewer** role to read and **Editor** to change anything; updateBoard and deleteBoard need **Owner**.

The browser starts sign-in by navigating to `GET /api/auth/login/{provider}?returnUrl=/...`. It is left out of the OpenAPI document because it is a redirect, not an API call, and so is the form post `POST /api/auth/link/{provider}` that connects another sign-in to the signed-in user. The provider redirects back to `/api/auth/callback/{provider}`.

`GET /health` returns `Healthy` when the API is up.

## Live updates

Instead of polling getBoard, a client can follow a board live over **SignalR** at `/api/live` (WebSockets, falling back to server-sent events or long polling). It authenticates like the rest of the API: a session cookie from PrikBoard's own pages, or `Authorization: Bearer <token>` with `boards:read` or `boards:write`. Changes still go through the REST endpoints; the hub only tells you about them. Official SignalR clients exist for JavaScript, .NET, Java and Swift.

| Hub method | Who | What |
| --- | --- | --- |
| `Join(boardId)` | Members | Follow one board (and stop following the previous one). Returns `{boardId, role, key, viewers}`; fails for boards you are not a member of |
| `Leave()` | Anyone | Stop following the board |
| `Drag({kind, id, dx, dy})` | Editors (tokens need `boards:write`) | Show others a drag in progress: `kind` is `Note`, `Bucket`, `Resize`, `Image` or `ImageResize`, and `dx`/`dy` the pointer travel in canvas pixels. Send at most every 50 ms |
| `DragEnd()` | Editors | The drag is over |

| Server message | Meaning |
| --- | --- |
| `BoardChanged({boardId, by})` | Something on the joined board was saved; call getBoard |
| `Presence({boardId, viewers})` | Who is on the board now (`principalId`, `kind`, `displayName`), each person once |
| `Dragging({key, principalId, displayName, kind, id, dx, dy})` | Someone else's drag in progress |
| `DragEnded(key)` | They let go. Keep the item where it was until the next getBoard shows where it landed |
| `BoardsChanged()` | Your list of boards changed (renamed, deleted, you were added or removed); call listBoards |
| `AccessChanged(boardId)` | Your role on that board changed or you lost access: call `Join` again (it fails when access is gone) |

- Changes you make yourself are announced to you too, unless you send your connection id in an `X-PrikBoard-Live` header with the change (the web app does, since it reloads after its own changes anyway).
- Being removed from a board stops its messages at once.
- Live updates are a convenience: reconnect when the connection drops, and call getBoard after reconnecting, because changes made in between are not replayed.

## Images

Images and GIFs live on the board canvas or are attached to a note (`noteId`), and getBoard lists them all under `images`. Upload the raw file as the request body, not as a form:

```bash
pb -X POST "localhost:5080/api/boards/{boardId}/images?fileName=ingest-errors.png&noteId={noteId}"   -H "Content-Type: image/png" --data-binary @ingest-errors.png
```

- **Formats.** PNG, JPEG, GIF and WebP up to 10 MB and 20000 pixels on a side. The server detects the format and pixel size from the file itself; SVG is refused because it can carry script.
- **Size.** `width` is the display width in canvas pixels, snapped to the grid and between 80 and 4000. The height always follows the aspect ratio. Without a width, an image keeps its own width up to 360.
- **Download** images from `contentUrl` with the same authentication as the rest of the API. The content of an image never changes, so it can be cached.

## Conventions

- **Placement.** createNote and moveNote take either a `bucketId` or both `x` and `y`, never both. With a `bucketId`, the note goes to the first free grid slot inside that bucket. A full bucket grows downwards to make room.
- **Ordering.** getBoard and listNotes return notes bucket by bucket, in the order of `buckets`, with loose notes last. Each group runs top to bottom, then left to right. Clients draw notes in `updatedAt` order, so the most recently moved or edited note is on top.
- **Sizes.** Buckets are at least 240×160 px, room for one note with a gap around it. Smaller sizes are raised to the minimum after snapping.
- **PATCH** only changes the fields you send. Sending `""` for an optional text field (`description`, `body`) clears it. A list you send (`tags`, `assignees`, `checklist`, `links`) replaces the whole list, and `[]` clears it. To tick off one checklist item, send the full checklist with that item changed.
- **Tags** are trimmed, lower-cased and hyphenated (`"Back end"` becomes `"back-end"`), at most 10 per note and 30 characters each.
- **Assignees** have a `name` and a `kind`, `Person` or `Agent`. People show as initials, agents as a sparkle. At most 10 per note.
- **Links** need an absolute `http` or `https` URL.
- **Colours.** Note fills are `Butter` (yellow), `Mint` (green), `Sky` (blue), `Coral` (salmon red) and `Lilac` (purple). Buckets also have a `color` (`Blue`, `Amber`, `Violet`, `Teal` or `Rose`); the web UI doesn't show it at the moment.
- **Errors** use `application/problem+json`. Validation errors (400) list the offending fields under `errors`, for example a `bucketId` that is not on the note's board. A 401 means the request is not signed in or the token is invalid. A 403 means your role or the token's scope is too low. A 404 means the resource does not exist or you are not a member of its board. A 429 means you hit a rate limit (email lookups and invite links).
