# PrikBoard API guide

This guide is for people and AI agents. The machine-readable contract is the OpenAPI 3.1 document at `/openapi/v1.json`. Point an agent framework or a client generator at it.

To explore and try the API in the browser, open the Scalar API reference at `/scalar` (in development http://localhost:5173/scalar, through the Vite proxy). It sends you through the default sign-in provider (GitHub) first, so "Test Request" runs with your session. To act as a machine instead, paste an access token into its Bearer field.

- **Base URL (local):** `http://localhost:5080`
- **Format:** JSON with camelCase properties. Enums such as `color` are strings.
- **Authentication:** every `/api` endpoint needs a caller, except `listAuthProviders` and `logout`. Machines send an OAuth 2.0 access token: `Authorization: Bearer <token>`. The OpenAPI document and `/health` are public.

## Authentication

People sign in in the browser through an identity provider (GitHub by default, or Google, Microsoft, Auth0 and others) and then hold a session cookie. Machines and agents never go through that flow. They get an access token from a trusted identity provider, usually with the **Client Credentials** grant, for the audience `api://prikboard`.

- `boards:read` allows `GET` requests, and `boards:write` allows everything.
- A token without the needed scope gets `403`. A missing, invalid, expired or untrusted token gets `401` with `WWW-Authenticate: Bearer`.
- Call `whoAmI` (`GET /api/me`) to check a token and see its scopes.
- Boards are shared: every caller with access sees every board.

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
| whoAmI | `GET /api/me` | Who the request is authenticated as (`Session` or `Token`) and its scopes |

The browser starts sign-in by navigating to `GET /api/auth/login/{provider}?returnUrl=/...`. It is left out of the OpenAPI document because it is a redirect, not an API call. The provider redirects back to `/api/auth/callback/{provider}`.

`GET /health` returns `Healthy` when the API is up.

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
- **Errors** use `application/problem+json`. Validation errors (400) list the offending fields under `errors`, for example a `bucketId` that is not on the note's board. A 401 means the request is not signed in or the token is invalid, and a 403 means the token lacks the scope. A 404 means the resource does not exist.
