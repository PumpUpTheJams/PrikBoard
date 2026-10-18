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
Board 1──* Column 1──* Note
```

- **Board.** One project, with a name and an optional description. New boards start with the columns *To do*, *Doing* and *Done*.
- **Column.** An ordered lane on a board. A column can only be deleted when it is empty.
- **Note.** A post-it with a title, an optional body and a colour (`Yellow`, `Pink`, `Blue`, `Green`, `Orange` or `Purple`). It is ordered within its column.

Columns and notes have a zero-based `position`. The server owns the ordering: when an item is inserted or moved, the server renumbers its siblings to 0..n-1.

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
- `src/components/`: `BoardView` (columns and drag and drop), `NoteCard` (view and inline edit) and `QuickAdd` (inline "+ Add" forms).
- After every change the board is reloaded from the API, so the UI always shows the server's ordering.
- In development, Vite proxies `/api` and `/openapi` to `http://localhost:5080`. Set `PRIKBOARD_API_URL` to point the proxy somewhere else.

## Tests

`src/PrikBoard.Api.Tests` runs the real API in memory through `WebApplicationFactory`, with a throwaway SQLite file for each test class. The tests cover behaviour through HTTP, the same way clients use the API. One test checks that the OpenAPI document keeps its operationIds.

## Not decided yet

- **Authentication and authorization.** This is needed before the API is exposed publicly. Agents will probably use API keys or tokens.
- **Hosting, and SQLite versus a managed database.** See [`infra/README.md`](../infra/README.md).
- **Richer project-management fields.** For example assignee, due date and labels.
- **An MCP server.** It would sit on top of the same endpoints, so AI assistants can use PrikBoard as a native tool.
