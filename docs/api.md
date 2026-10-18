# PrikBoard API guide

This guide is for people and AI agents. The machine-readable contract is the OpenAPI 3.1 document at `/openapi/v1.json`. Point an agent framework or a client generator at it.

- **Base URL (local):** `http://localhost:5080`
- **Format:** JSON with camelCase properties. Enums such as `color` are strings.
- **Authentication:** none yet. Run the API locally only.

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
# Create a board (starts with the buckets To do / Doing / Done)
curl -X POST localhost:5080/api/boards -H "Content-Type: application/json" \
  -d '{"name":"Website relaunch"}'

# Add a note (lands in the first bucket)
curl -X POST localhost:5080/api/boards/{boardId}/notes -H "Content-Type: application/json" \
  -d '{"title":"Pick a colour palette","color":"Coral","tags":["design"]}'

# Claim it as an agent and tick off part of the checklist
curl -X PATCH localhost:5080/api/notes/{noteId} -H "Content-Type: application/json" \
  -d '{"assignees":[{"name":"Claude","kind":"Agent"}],"agentWorking":true,
       "checklist":[{"text":"Three options","done":true},{"text":"Team vote","done":false}]}'

# Move it into another bucket
curl -X POST localhost:5080/api/notes/{noteId}/move -H "Content-Type: application/json" \
  -d '{"bucketId":"{doneBucketId}"}'

# Or put it at exact coordinates (its bucket follows from where it lands)
curl -X POST localhost:5080/api/notes/{noteId}/move -H "Content-Type: application/json" \
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
| deleteNote | `DELETE /api/notes/{noteId}` | Delete a note |

`GET /health` returns `Healthy` when the API is up.

## Conventions

- **Placement.** createNote and moveNote take either a `bucketId` or both `x` and `y`, never both. With a `bucketId`, the note goes to the first free grid slot inside that bucket. A full bucket grows downwards to make room.
- **Ordering.** getBoard and listNotes return notes bucket by bucket, in the order of `buckets`, with loose notes last. Each group runs top to bottom, then left to right. Clients draw notes in `updatedAt` order, so the most recently moved or edited note is on top.
- **Sizes.** Buckets are at least 240×160 px, room for one note with a gap around it. Smaller sizes are raised to the minimum after snapping.
- **PATCH** only changes the fields you send. Sending `""` for an optional text field (`description`, `body`) clears it. A list you send (`tags`, `assignees`, `checklist`, `links`) replaces the whole list, and `[]` clears it. To tick off one checklist item, send the full checklist with that item changed.
- **Tags** are trimmed, lower-cased and hyphenated (`"Back end"` becomes `"back-end"`), at most 10 per note and 30 characters each.
- **Assignees** have a `name` and a `kind`, `Person` or `Agent`. People show as initials, agents as a sparkle. At most 10 per note.
- **Links** need an absolute `http` or `https` URL.
- **Colours.** Note fills are `Butter` (yellow), `Mint` (green), `Sky` (blue), `Coral` (salmon red) and `Lilac` (purple). Buckets also have a `color` (`Blue`, `Amber`, `Violet`, `Teal` or `Rose`); the web UI doesn't show it at the moment.
- **Errors** use `application/problem+json`. Validation errors (400) list the offending fields under `errors`, for example a `bucketId` that is not on the note's board. A 404 means the resource does not exist.
