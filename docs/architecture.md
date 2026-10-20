# Architecture

## Overview

```
 Browser (React SPA) ──┐
                       ├──►  /api/*   ASP.NET Core minimal API  ──►  EF Core  ──►  PostgreSQL
 AI agent / script  ───┘     /openapi/v1.json  (contract)
                             /api/live  SignalR hub (live updates, presence, drags)
                             /mcp       MCP server (tools for agents)
```

There is one API, and nothing is private to the UI. Anything a person can do in the browser, an AI agent can do over HTTP with the same endpoints. The OpenAPI document is the contract between them.

People sign in through an identity provider and hold a session cookie. Machines and agents send an OAuth 2.0 access token (usually from the Client Credentials grant) from a trusted identity provider, limited by the scopes `boards:read` and `boards:write`. See [Authentication](authentication.md).

## Domain model

```
Board 1──* Bucket
Board 1──* Note *──0..1 Bucket
Board 1──* BoardTag
Board 1──* Image *──0..1 Note
Board 1──* BoardMember *──1 Principal (User | Agent)
Board 1──* BoardInvite
User 1──* UserIdentity
```

- **Board.** One project and a freeform canvas, with a name and an optional description. New boards start with the buckets *To do*, *Doing* and *Done* side by side.
- **Bucket.** A named rectangle on the board (`x`, `y`, `width`, `height`). Deleting a bucket leaves its notes on the board. Deleted buckets, notes, images and tags can be restored for 7 days (see Soft delete below).
- **Note.** A 200×120 px post-it at `x`, `y`. It has a title, a fill (`Butter`, `Mint`, `Sky`, `Coral` or `Lilac`), tags, assignees (people or agents), an `AgentWorking` flag that an agent sets while it works on the note, who created it (`CreatedById`, null for older notes) and its content as blocks. Tag names and assignee principal ids are PostgreSQL arrays, always read and replaced whole.
- **Note block.** A piece of a note's content on the note's own canvas: `Text`, `Checklist` (items) or `Links` (titled links), with an optional title and a place (`x`, `y`, `width`, `height` in note pixels on the 20 px grid). A note's blocks are one `jsonb` column (`Data/NoteBlocksJson.cs`). Each block is changed on its own (add, update, delete), with the note's row locked, so concurrent edits to different blocks don't overwrite each other.
- **BoardTag.** A tag defined on a board (at most 100). Notes store the names of the board's tags they have; a new name on a note adds it to the board. Renaming or deleting a tag rewrites every note on the board in the same transaction (`array_replace`, `array_remove`).
- **Image.** A PNG, JPEG, GIF or WebP file on the board (`x`, `y`, display `width`; the height follows the aspect ratio) or attached to a note, where `x` and `y` are on the note's canvas. The file is kept on disk (see Files below), not in PostgreSQL. The server reads the format and pixel size from the file header (`Domain/ImageFormats.cs`) and never trusts the declared type.
- **Principal.** Anyone who can be a board member: a GUID plus an optional display name they chose. Stored in one `Principals` table (TPH, discriminator `Kind`):
  - a **User** is a person. Each of their sign-ins is a **UserIdentity** holding the provider and keyed hashes of the subject id and verified email (`Auth/KeyedHasher.cs`), never the raw values;
  - an **Agent** is a token client, identified by issuer id and client id, and created the first time its token is seen (`Auth/AgentRegistry.cs`).
- **BoardMember.** A principal's role on a board: `Viewer`, `Editor` or `Owner`. Boards are members-only.
- **BoardInvite.** A single-use, expiring invite link with a role. Only the SHA-256 of its token is stored.

Assignees are principals, stored by id and shown with their current name. Who can be added is the board's members plus the agents of members' personal access tokens that reach the board (`BoardAccess.AssignableIds`). Someone who loses access stays on the notes they had until they are taken off.

The server owns the geometry (`Domain/Layout.cs`). It snaps every coordinate to a 20 px grid. A note's `BucketId` is not chosen directly. It is always the topmost bucket containing the note's centre (buckets stack in creation order), and the server recomputes it whenever a note or bucket is created, moved, resized or deleted. Moving a bucket moves its notes with it; resizing it from a top or left corner or edge (a new place together with a new size) leaves them where they are. A client that only knows a bucket, such as an agent, can move a note there with `bucketId`, and the server picks a free grid slot inside it. When a bucket is full, it grows downwards to make room.

## Backend: `src/PrikBoard.Api`

| Path | Responsibility |
| --- | --- |
| `Auth/` | Authentication schemes, sign-in providers, token issuers and scopes, sign-in mapping |
| `Domain/` | Entities and the positioning helper |
| `Data/` | `PrikBoardDbContext` and EF Core migrations |
| `Contracts/` | Request and response records. Their XML doc comments become OpenAPI descriptions |
| `Endpoints/` | One file per resource with route mappings and handlers |
| `Program.cs` | Composition: services, OpenAPI and the Scalar API reference (`/scalar`), migrations on startup, route groups |

The API is built on these conventions:

- **Minimal APIs with `TypedResults`.** Response types and status codes show up accurately in OpenAPI.
- **Every endpoint has an `operationId`.** For example `getBoard` or `moveNote`, so generated clients and AI tool definitions get stable names.
- **Geometry lives in `Domain/Layout.cs`.** Snapping, bucket membership, stacking order and free-spot search are in one place. `src/web/src/board/layout.ts` mirrors the parts the UI needs for live feedback while dragging.
- **Validation and errors.** Requests are validated with DataAnnotations through .NET 10's built-in minimal API validation. Errors are returned as RFC 9457 `application/problem+json`.
- **PATCH for partial updates.** Omitted fields stay unchanged.
- **Board access is declared per endpoint.** `Auth/BoardAccess.cs` adds `.RequireBoardRole(role, BoardScope.X)`, an endpoint filter that finds the board from the route id (board, bucket, note or image) and the caller's membership in one query. Non-members get 404, too-low roles 403. Endpoints without a board use `.NoBoardScope()`, and a test fails if an `/api` endpoint has neither.
- **No personal data.** Names, emails and provider subject ids from SSO are never stored, except a copy of a person's GitHub picture as avatar (never its URL), which they can remove; see [Authentication](authentication.md#users-linked-sign-ins-and-personal-data).
- **Personal access tokens** (`Auth/AccessTokens.cs`, `Auth/AccessTokenHandler.cs`). A token is its own agent, with its owner's board access capped by `BoardCaller`. `BoardAccess.AuthorizeAsync` applies the cap for REST, the live hub and MCP alike.
- **MCP** (`Mcp/`). `ModelContextProtocol.AspNetCore`, stateless Streamable HTTP at `/mcp`. `BoardTools` are curated tools that call the REST handlers (made `internal` for this) after the same `BoardAccess.AuthorizeAsync` check, validate with the contracts' DataAnnotations, and announce changes through `BoardEvents`. A board's agent rules (Markdown, owners only) are the exception to "note text is data": the server instructions, `get_board`, `get_board_rules` and a reminder after every write tell agents to follow them.
- **Live updates** (`Live/`). `BoardHub` is a SignalR hub at `/api/live`. A connection joins one board at a time, after the same membership check as the REST API; `LiveViewers` keeps who is connected to which board with what role.
  - `BoardAccess` announces every successful board-scoped write to the board's group (`BoardEvents.ChangedAsync`), so endpoints don't have to. Endpoints that change board lists or roles also call `BoardEvents` (renames, deletes, members, invites, display names).
  - Drags in progress are relayed between clients as gesture deltas, never stored. Only the drop is saved, through REST.
  - Live state lives in memory, so this works for one API instance. Several instances would need a SignalR backplane (Redis or Azure SignalR) and shared presence.
- **Soft delete** (`Data/Tombstones.cs`). Deleting a note, bucket, image or tag sets its `DeletedAt` instead of removing the row. A named EF query filter (`PrikBoardDbContext.SoftDelete`) hides it from every query. The restore endpoints bring it back with the same id, so links, stacking order and creators survive an undo. A note's images share its stamp and come back with it. A deleted tag remembers the notes it was taken off (`RemovedFromNoteIds`). Raw SQL ignores the filter, so tag renames and deletes reach deleted notes too. `BoardAccess.AuthorizeAsync` also ignores the filter, so restores are authorized like other writes. `TombstonePurger` deletes for good whatever was deleted more than 7 days ago, once an hour.
- **Files** (`Data/FileStore.cs`). Image and avatar bytes are files under `Storage:Path`, a volume of the API's own, each with a `StoredFile` row that holds its original name, content type, size, SHA-256 and date. On disk a file is named after its row's id, never after what is in it, in a folder per board (`boards/{boardId}/`; avatars in `avatars/`), and its content never changes. Downloads come from disk (small ones from memory), with the id as ETag and immutable private caching, shown inline but saved under the original name. After each purge, `FileStore.SweepAsync` deletes the files nothing refers to any more. The bytes used to live in PostgreSQL; `Data/StartupMigration.cs` moves whatever is left there to disk before the migration that drops those tables.
- **Migrations on startup.** They are applied automatically, which is fine for a single instance. Revisit this once the API runs as several instances.

To change the schema, edit the entities or `PrikBoardDbContext`, then add a migration. This uses the `dotnet-ef` tool pinned in `.config/dotnet-tools.json`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/PrikBoard.Api --output-dir Data/Migrations
```

## Frontend: `src/web`

React 19, TypeScript and Vite, with no state library and no UI kit. The visual design is the "Night shift" mockup in [`docs/Scrum Board — Night shift.html`](Scrum%20Board%20—%20Night%20shift.html), with Geist and Geist Mono as typefaces.

- `src/api/`: a typed `fetch` client whose method names match the API's operationIds.
- `src/board/useBoard.ts`: loads the open board, runs every change and reloads afterwards, and reloads when told to by live updates. It also polls: every few seconds while live updates are down, every 30 seconds as a safety net otherwise. Refreshes wait while something is being dragged.
- `src/live/useLive.ts`: the SignalR connection (`@microsoft/signalr`). It joins the open board, reports who else is here (`components/Presence`), and sends and receives drags in progress, which `BoardCanvas` applies to the layout like its own gesture, outlined in the person's colour.
- `src/board/BoardCanvas.tsx`: the zoomable canvas and its pointer gestures (move, pan, draw and resize buckets, stick post-its by clicking with the post-it tool or by dragging its icon from the rail), plus the floating `ToolRail`, `ViewControls` (zoom and minimap) and `Inspector` (rename or delete a bucket).
- `src/board/PostIt.tsx`: the post-it as shown on the board, with its title, tags, assignee stack and states (hover, selected, dragging, agent working, faded by the tag filter or the search). For editors, its tags and assignees open the tag or people window straight from the board, and a click on the title edits it in place, with the text cursor where you clicked (Enter or clicking away saves, Escape cancels). A double-click elsewhere on the post-it expands it. Dropping a post-it on another post-it (or on the open post-it's window) adds a link to it to its links block (a new one only when it has none) instead of moving it, and dropping a board image on a post-it attaches it.
- `src/components/PickerWindow.tsx`: the search window behind `board/TagPicker` (the board's tags, with rename and delete) and `board/AssigneePicker` (the board's people, five at a time). It is portalled to the page so the zoom doesn't scale it, and keeps focus in its field so the board's shortcuts stay quiet.
- `src/board/TagFilter.tsx`: the pill at the bottom left that fades post-its without any of the picked tags.
- `src/board/BoardSearch.tsx`: the search at the top of the board, opened with `/` or Ctrl/Cmd+F (or the rail's search button). It looks in titles, tags, people, who created a post-it and its blocks (`search.ts`: case and accents are ignored, every word has to match). Post-its without the words fade like the tag filter fades them (the two combine), the words are marked where they show (`Highlight.tsx`), and the matches are listed under the field with where they matched. ↑/↓ move through them while the board scrolls to each one, Enter opens one, and Escape closes the post-it and goes back to the results.
- `src/board/BoardImageView.tsx`: an image on the board, moved by dragging and resized from its corners or edges (`ResizeHandles.tsx`: corner squares that resize both ways and invisible edge strips that resize one way, shown on every selected item that can be resized: buckets, images and blocks; post-its have a fixed size and only get the outline). Images are added with the Image button (or `I`), by pasting, or by dropping files on the board; pasting or dropping into an expanded post-it attaches them to it. Dragging an image into the expanded post-it attaches it, and dragging it out puts it back on the board.
- `src/board/ExpandedNote.tsx`: an expanded post-it, a window floating over the board (`useFloatingWindow`: moved by its band, resized from its edges, size and place kept in localStorage). The board stays usable and is never dimmed. A header band in its fill holds the title, tags, assignees and who created it.
- `src/board/NoteCanvas.tsx`: the post-it's own canvas inside that window, with zoom and pan. Its blocks (`NoteBlocks.tsx`) and images are moved, resized, added and removed like items on the board; `NoteBar.tsx` along the bottom looks like the tool rail. It exposes a handle so the board can drop images into it and route pastes.
- `src/board/layout.ts`: grid, bucket and note-canvas rules mirrored from the server.
- `src/board/undo.ts` and `useUndo.ts`: undo and redo. Each change on the board is recorded with what it touched before and after; undo checks against a fresh getBoard that nothing changed since (otherwise it says why and drops the step) and writes the old values back, restoring deleted things through the restore endpoints. The history is kept per board in the tab, in memory. Deletes therefore don't ask first; they show a toast with *Undo*.
- `src/components/`: `Avatar` (people and agents), `Menu` (the board and settings menus), `ConfirmDialog` (asks before deleting a board or other things that can't be undone), `Toast` (short messages, optionally with an action) and `QuickAdd` (inline add forms).
- During a drag the UI shows the snapped position locally. On drop it sends one request and reloads the board, so it always ends up showing the server's layout.
- Dark ("night shift") is the only theme.
- In development, Vite proxies `/api` and `/openapi` to `http://localhost:5080`. Set `PRIKBOARD_API_URL` to point the proxy somewhere else.

## Tests

`src/PrikBoard.Api.Tests` runs the real API in memory through `WebApplicationFactory`, with a throwaway PostgreSQL database for each test class, created on the `compose.yaml` server. The tests cover behaviour through HTTP, the same way clients use the API. One test checks that the OpenAPI document keeps its operationIds.

## Not decided yet

- **OAuth for MCP connectors.** claude.ai and Claude Desktop connectors need an OAuth flow (protected resource metadata, an authorization server), not a pasted token. See [the design](design-access-tokens-and-mcp.md#3-phases).
- **Scaling out live updates.** The hub and its presence list live in one API process; a second instance needs a backplane.
- **Hash key rotation.** Losing or changing `Auth:HashKey` orphans every account. Rotation would need a second key and re-hashing on sign-in.
- **Account deletion.** People can unlink sign-ins and leave boards, but cannot delete their account yet.
- **Hosting, and which managed PostgreSQL to use.** See [`infra/README.md`](../infra/README.md).
- **Drawing.** The mockup also shows freehand drawing on the board and annotations on images. Images are in place; drawing is not built yet.
- **Image storage at scale.** Images and avatars are files on a volume of the API's own (`Data/FileStore.cs`), which suits a single instance. Several instances would need a shared volume or object storage (for example Azure Blob Storage) behind the same `FileStore`.
- **More project-management fields.** For example a due date.
- **An MCP server.** It would sit on top of the same endpoints, so AI assistants can use PrikBoard as a native tool.
