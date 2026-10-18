# Architecture

## Overview

```
 Browser (React SPA) ──┐
                       ├──►  /api/*   ASP.NET Core minimal API  ──►  EF Core  ──►  SQLite
 AI agent / script  ───┘     /openapi/v1.json  (contract)
```

There is one API, and nothing is private to the UI. Anything a person can do in the browser, an AI agent can do over HTTP with the same endpoints. The OpenAPI document is the contract between them.

## Domain model

```
Board 1──* Bucket
Board 1──* Note *──0..1 Bucket
```

- **Board.** One project and a freeform canvas, with a name and an optional description. New boards start with the buckets *To do*, *Doing* and *Done* side by side.
- **Bucket.** A named rectangle on the board (`x`, `y`, `width`, `height`). Deleting a bucket leaves its notes on the board.
- **Note.** A 200×120 px post-it at `x`, `y`. It has a title, an optional body, a fill (`Butter`, `Mint`, `Sky`, `Coral` or `Lilac`), tags, assignees (people or agents), a checklist, links, and an `AgentWorking` flag that an agent sets while it works on the note. The lists are stored as JSON columns on the note and are always read and replaced whole.

The server owns the geometry (`Domain/Layout.cs`). It snaps every coordinate to a 20 px grid. A note's `BucketId` is not chosen directly. It is always the topmost bucket containing the note's centre (buckets stack in creation order), and the server recomputes it whenever a note or bucket is created, moved, resized or deleted. Moving a bucket moves its notes with it. A client that only knows a bucket, such as an agent, can move a note there with `bucketId`, and the server picks a free grid slot inside it. When a bucket is full, it grows downwards to make room.

## Backend: `src/PrikBoard.Api`

| Path | Responsibility |
| --- | --- |
| `Domain/` | Entities and the positioning helper |
| `Data/` | `PrikBoardDbContext` and EF Core migrations |
| `Contracts/` | Request and response records. Their XML doc comments become OpenAPI descriptions |
| `Endpoints/` | One file per resource with route mappings and handlers |
| `Program.cs` | Composition: services, OpenAPI, migrations on startup, route groups |

The API is built on these conventions:

- **Minimal APIs with `TypedResults`.** Response types and status codes show up accurately in OpenAPI.
- **Every endpoint has an `operationId`.** For example `getBoard` or `moveNote`, so generated clients and AI tool definitions get stable names.
- **Geometry lives in `Domain/Layout.cs`.** Snapping, bucket membership, stacking order and free-spot search are in one place. `src/web/src/board/layout.ts` mirrors the parts the UI needs for live feedback while dragging.
- **Validation and errors.** Requests are validated with DataAnnotations through .NET 10's built-in minimal API validation. Errors are returned as RFC 9457 `application/problem+json`.
- **PATCH for partial updates.** Omitted fields stay unchanged.
- **Migrations on startup.** They are applied automatically, which is fine for a single instance. Revisit this once the API runs as several instances.

To change the schema, edit the entities or `PrikBoardDbContext`, then add a migration. This uses the `dotnet-ef` tool pinned in `.config/dotnet-tools.json`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/PrikBoard.Api --output-dir Data/Migrations
```

## Frontend: `src/web`

React 19, TypeScript and Vite, with no state library and no UI kit. The visual design is the "Night shift" mockup in [`docs/Scrum Board — Night shift.html`](Scrum%20Board%20—%20Night%20shift.html), with Geist and Geist Mono as typefaces.

- `src/api/`: a typed `fetch` client whose method names match the API's operationIds.
- `src/board/useBoard.ts`: loads the open board, runs every change and reloads afterwards, and re-reads the board every few seconds so changes made by agents show up. Refreshes pause while something is being dragged.
- `src/board/BoardCanvas.tsx`: the zoomable canvas and its pointer gestures (move, pan, draw and resize buckets, stick post-its), plus the floating `ToolRail`, `ViewControls` (zoom and minimap) and `Inspector` (rename or delete a bucket).
- `src/board/PostIt.tsx`: the post-it as shown on the board, with its title, tags, assignee stack and states (hover, selected, dragging, agent working).
- `src/board/ExpandedNote.tsx`: an expanded post-it. A header band in its fill holds the title, tags and assignees. Below it, a mini board holds the Note, Checklist and Links widgets.
- `src/board/layout.ts`: grid and bucket rules mirrored from the server.
- `src/components/`: `Avatar` (people and agents), `Menu` (the board and settings menus) and `QuickAdd` (inline add forms).
- During a drag the UI shows the snapped position locally. On drop it sends one request and reloads the board, so it always ends up showing the server's layout.
- Dark ("night shift") is the default theme. The light theme is a `data-theme="light"` override, remembered in `localStorage`.
- In development, Vite proxies `/api` and `/openapi` to `http://localhost:5080`. Set `PRIKBOARD_API_URL` to point the proxy somewhere else.

## Tests

`src/PrikBoard.Api.Tests` runs the real API in memory through `WebApplicationFactory`, with a throwaway SQLite file for each test class. The tests cover behaviour through HTTP, the same way clients use the API. One test checks that the OpenAPI document keeps its operationIds.

## Not decided yet

- **Authentication and authorization.** This is needed before the API is exposed publicly. Agents will probably use API keys or tokens.
- **Hosting, and SQLite versus a managed database.** See [`infra/README.md`](../infra/README.md).
- **Images and drawing.** The mockup also shows pasted images and GIFs, and freehand drawing on the board and on images. These need file storage first.
- **People.** Assignees are free-text names. There are no user accounts yet, so the settings menu has no profile.
- **More project-management fields.** For example a due date.
- **An MCP server.** It would sit on top of the same endpoints, so AI assistants can use PrikBoard as a native tool.
