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
- **Bucket.** A named rectangle on the board (`x`, `y`, `width`, `height`). Deleting a bucket leaves its notes on the board.
- **Note.** A 200×120 px post-it at `x`, `y`. It has a title, an optional body, a fill (`Butter`, `Mint`, `Sky`, `Coral` or `Lilac`), tags, assignees (people or agents), a checklist, links, an `AgentWorking` flag that an agent sets while it works on the note, and who created it (`CreatedById`, null for older notes). Tag names and assignee principal ids are PostgreSQL arrays and the other lists are `jsonb` columns on the note; all of them are always read and replaced whole.
- **BoardTag.** A tag defined on a board (at most 100). Notes store the names of the board's tags they have; a new name on a note adds it to the board. Renaming or deleting a tag rewrites every note on the board in the same transaction (`array_replace`, `array_remove`).
- **Image.** A PNG, JPEG, GIF or WebP file on the canvas (`x`, `y`, display `width`; the height follows the aspect ratio) or attached to a note. The bytes are stored in PostgreSQL (`bytea`) in their own `ImageContents` table, so board snapshots never load them. The server reads the format and pixel size from the file header (`Domain/ImageFormats.cs`) and never trusts the declared type.
- **Principal.** Anyone who can be a board member: a GUID plus an optional display name they chose. Stored in one `Principals` table (TPH, discriminator `Kind`):
  - a **User** is a person. Each of their sign-ins is a **UserIdentity** holding the provider and keyed hashes of the subject id and verified email (`Auth/KeyedHasher.cs`), never the raw values;
  - an **Agent** is a token client, identified by issuer id and client id, and created the first time its token is seen (`Auth/AgentRegistry.cs`).
- **BoardMember.** A principal's role on a board: `Viewer`, `Editor` or `Owner`. Boards are members-only.
- **BoardInvite.** A single-use, expiring invite link with a role. Only the SHA-256 of its token is stored.

Assignees are principals, stored by id and shown with their current name. Who can be added is the board's members plus the agents of members' personal access tokens that reach the board (`BoardAccess.AssignableIds`). Someone who loses access stays on the notes they had until they are taken off.

The server owns the geometry (`Domain/Layout.cs`). It snaps every coordinate to a 20 px grid. A note's `BucketId` is not chosen directly. It is always the topmost bucket containing the note's centre (buckets stack in creation order), and the server recomputes it whenever a note or bucket is created, moved, resized or deleted. Moving a bucket moves its notes with it. A client that only knows a bucket, such as an agent, can move a note there with `bucketId`, and the server picks a free grid slot inside it. When a bucket is full, it grows downwards to make room.

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
- **No personal data.** Names, emails and provider subject ids from SSO are never stored; see [Authentication](authentication.md#users-linked-sign-ins-and-personal-data).
- **Personal access tokens** (`Auth/AccessTokens.cs`, `Auth/AccessTokenHandler.cs`). A token is its own agent, with its owner's board access capped by `BoardCaller`. `BoardAccess.AuthorizeAsync` applies the cap for REST, the live hub and MCP alike.
- **MCP** (`Mcp/`). `ModelContextProtocol.AspNetCore`, stateless Streamable HTTP at `/mcp`. `BoardTools` are curated tools that call the REST handlers (made `internal` for this) after the same `BoardAccess.AuthorizeAsync` check, validate with the contracts' DataAnnotations, and announce changes through `BoardEvents`.
- **Live updates** (`Live/`). `BoardHub` is a SignalR hub at `/api/live`. A connection joins one board at a time, after the same membership check as the REST API; `LiveViewers` keeps who is connected to which board with what role.
  - `BoardAccess` announces every successful board-scoped write to the board's group (`BoardEvents.ChangedAsync`), so endpoints don't have to. Endpoints that change board lists or roles also call `BoardEvents` (renames, deletes, members, invites, display names).
  - Drags in progress are relayed between clients as gesture deltas, never stored. Only the drop is saved, through REST.
  - Live state lives in memory, so this works for one API instance. Several instances would need a SignalR backplane (Redis or Azure SignalR) and shared presence.
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
- `src/board/BoardCanvas.tsx`: the zoomable canvas and its pointer gestures (move, pan, draw and resize buckets, stick post-its), plus the floating `ToolRail`, `ViewControls` (zoom and minimap) and `Inspector` (rename or delete a bucket).
- `src/board/PostIt.tsx`: the post-it as shown on the board, with its title, tags, assignee stack and states (hover, selected, dragging, agent working, faded by the tag filter). For editors, its tags and assignees open the tag or people window straight from the board, and a click on the title edits it in place, with the text cursor where you clicked (Enter or clicking away saves, Escape cancels). A double-click elsewhere on the post-it expands it.
- `src/components/PickerWindow.tsx`: the search window behind `board/TagPicker` (the board's tags, with rename and delete) and `board/AssigneePicker` (the board's people, five at a time). It is portalled to the page so the zoom doesn't scale it, and keeps focus in its field so the board's shortcuts stay quiet.
- `src/board/TagFilter.tsx`: the pill at the bottom left that fades post-its without any of the picked tags.
- `src/board/BoardImageView.tsx`: an image on the board, moved by dragging and resized from its corner handle. Images are added with the Image button (or `I`), by pasting, or by dropping files on the board; pasting or dropping into an expanded post-it attaches them to it.
- `src/board/ExpandedNote.tsx`: an expanded post-it. A header band in its fill holds the title, tags, assignees and who created it. Below it, a mini board holds the Note, Checklist and Links widgets.
- `src/board/layout.ts`: grid and bucket rules mirrored from the server.
- `src/components/`: `Avatar` (people and agents), `Menu` (the board and settings menus), `ConfirmDialog` (asks before deleting a post-it or board) and `QuickAdd` (inline add forms).
- During a drag the UI shows the snapped position locally. On drop it sends one request and reloads the board, so it always ends up showing the server's layout.
- Dark ("night shift") is the default theme. The light theme is a `data-theme="light"` override, remembered in `localStorage`.
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
- **Image storage at scale.** Images are stored in PostgreSQL, which is simple and fine for small teams. Very large boards may later want object storage (for example Azure Blob Storage) behind the same API.
- **More project-management fields.** For example a due date.
- **An MCP server.** It would sit on top of the same endpoints, so AI assistants can use PrikBoard as a native tool.
