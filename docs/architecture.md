# Architecture

## Overview

```
 Browser (React SPA) ──┐
                       ├──►  /api/*   ASP.NET Core minimal API  ──►  EF Core  ──►  PostgreSQL
 AI agent / script  ───┘     /openapi/v1.json  (contract)
```

There is one API, and nothing is private to the UI. Anything a person can do in the browser, an AI agent can do over HTTP with the same endpoints. The OpenAPI document is the contract between them.

People sign in through an identity provider and hold a session cookie. Machines and agents send an OAuth 2.0 access token (usually from the Client Credentials grant) from a trusted identity provider, limited by the scopes `boards:read` and `boards:write`. See [Authentication](authentication.md).

## Domain model

```
Board 1──* Bucket
Board 1──* Note *──0..1 Bucket
User
```

- **Board.** One project and a freeform canvas, with a name and an optional description. New boards start with the buckets *To do*, *Doing* and *Done* side by side.
- **Bucket.** A named, coloured rectangle on the board (`x`, `y`, `width`, `height`). Deleting a bucket leaves its notes on the board.
- **Note.** A 160×140 px post-it at `x`, `y`, with a title, an optional body and a colour (`Yellow`, `Pink`, `Blue`, `Green`, `Orange` or `Purple`).
- **User.** Someone who signed in, identified by provider plus subject id. Boards are not owned yet: every signed-in user sees every board.

The server owns the geometry (`Domain/Layout.cs`). It snaps every coordinate to a 20 px grid. A note's `BucketId` is not chosen directly. It is always the topmost bucket containing the note's centre (buckets stack in creation order), and the server recomputes it whenever a note or bucket is created, moved, resized or deleted. Moving a bucket moves its notes with it. A client that only knows a bucket, such as an agent, can move a note there with `bucketId`, and the server picks a free grid slot inside it.

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
- **Migrations on startup.** They are applied automatically, which is fine for a single instance. Revisit this once the API runs as several instances.

To change the schema, edit the entities or `PrikBoardDbContext`, then add a migration. This uses the `dotnet-ef` tool pinned in `.config/dotnet-tools.json`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/PrikBoard.Api --output-dir Data/Migrations
```

## Frontend: `src/web`

React 19, TypeScript and Vite, with no state library and no UI kit yet.

- `src/api/`: a typed `fetch` client whose method names match the API's operationIds.
- `src/board/`: `BoardCanvas` (the canvas, pointer gestures for moving notes and buckets, resizing and drawing buckets), `Toolbar` (Move, Draw bucket and Post-it tools), `Inspector` (edit the selected note or bucket) and `layout.ts` (grid and bucket rules mirrored from the server).
- `src/components/QuickAdd`: the inline "+ New board" form.
- During a drag the UI shows the snapped position locally. On drop it sends one request and reloads the board, so it always ends up showing the server's layout.
- Dark is the default theme. The light theme is a `data-theme="light"` override, remembered in `localStorage`.
- In development, Vite proxies `/api` and `/openapi` to `http://localhost:5080`. Set `PRIKBOARD_API_URL` to point the proxy somewhere else.

## Tests

`src/PrikBoard.Api.Tests` runs the real API in memory through `WebApplicationFactory`, with a throwaway PostgreSQL database for each test class, created on the `compose.yaml` server. The tests cover behaviour through HTTP, the same way clients use the API. One test checks that the OpenAPI document keeps its operationIds.

## Not decided yet

- **Authorization.** Authentication is in place, but every signed-in user (and every token with `boards:write`) can see and change every board. Board ownership or membership comes later.
- **Hosting, and which managed PostgreSQL to use.** See [`infra/README.md`](../infra/README.md).
- **Richer project-management fields.** For example assignee, due date and labels.
- **An MCP server.** It would sit on top of the same endpoints, so AI assistants can use PrikBoard as a native tool.
