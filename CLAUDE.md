# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

PrikBoard is a post-it board for small project management. It has a .NET 10 REST API (`src/PrikBoard.Api`) and a React 19 + Vite SPA (`src/web`). People in the browser and AI agents both use the same API, and the OpenAPI document at `/openapi/v1.json` is the contract between them. Background reading: `docs/architecture.md` (design and open decisions) and `docs/api.md` (endpoint table and conventions).

## Commands

```bash
dotnet run --project src/PrikBoard.Api            # API on http://localhost:5080; creates/migrates prikboard.db on startup
dotnet test PrikBoard.slnx                        # all API tests (what CI runs)
dotnet test PrikBoard.slnx --filter "FullyQualifiedName~Moving_a_bucket_carries_its_notes_along"   # one test

cd src/web && npm install && npm run dev          # SPA on http://localhost:5173, proxies /api and /openapi to :5080
cd src/web && npm run lint                        # oxlint (CI)
cd src/web && npm run build                       # tsc -b type check + vite build (CI)

dotnet tool restore                               # pinned dotnet-ef from .config/dotnet-tools.json
dotnet ef migrations add <Name> --project src/PrikBoard.Api --output-dir Data/Migrations
```

`.claude/launch.json` defines `api` and `web` preview servers. The frontend has no test suite. Set `PRIKBOARD_API_URL` to point the Vite proxy at a different API.

## Architecture

Domain: a board is a freeform canvas. `Board 1──* Bucket` (named rectangles) and `Board 1──* Note` (160×140 px post-its at x/y), and each note has an optional `BucketId`. A new board gets the buckets *To do*, *Doing* and *Done* side by side (`Board.DefaultBuckets`).

### API (`src/PrikBoard.Api`)

- **Minimal APIs, not controllers.** There is one file per resource in `Endpoints/`, each exposing a `Map*Endpoints(this RouteGroupBuilder)` extension that `Program.cs` calls on the `/api` group. Handlers are private static methods that take `PrikBoardDbContext` directly; there is no service layer. They return `TypedResults` with `Results<...>` union return types, so OpenAPI lists the exact status codes.
- **Every endpoint needs `.WithName("camelCaseOperationId")`**, plus `WithSummary`/`WithDescription`. These names are the stable tool names for AI clients and generated clients. A test asserts that key operationIds exist (`OpenApi_document_exposes_named_operations_for_ai_clients`).
- **XML doc comments on `Contracts/` records become OpenAPI descriptions** (`GenerateDocumentationFile` is on, CS1591 is suppressed). Write them for an AI reader. Validation uses DataAnnotations on record properties (`[property: Required, StringLength(...)]`) through .NET 10's built-in `AddValidation()`. Errors are RFC 9457 problem+json.
- **Contracts are separate from entities.** Response records have a static `From(entity)` mapper. Don't return `Domain/` entities from endpoints.
- **The server owns the geometry** (`Domain/Layout.cs`). Coordinates snap to a 20 px grid (`Layout.Snap`). `Note.BucketId` is derived, never set directly: it is the topmost bucket containing the note's centre, with buckets stacked by `CreatedAt`. Every create, move, resize or delete of a note or bucket must call `Layout.AssignBuckets`. Moving a bucket shifts its notes by the same delta. createNote and moveNote take either `bucketId` (the server picks `Layout.FreeSpotIn`) or both `x` and `y`, never both. `src/web/src/board/layout.ts` mirrors these rules for drag feedback, so keep the two in sync.
- **PATCH semantics.** A null field means unchanged. Optional text fields go through `PatchText.Apply`, where empty or whitespace clears the value (sets it to null). Moving a note is a separate `POST /notes/{id}/move` (`moveNote`), not part of `updateNote`. Buckets move and resize through `updateBucket`.
- **Error shapes.** A bucket that isn't on the note's board, or an invalid `bucketId`/`x`/`y` combination, is a 400 `ValidationProblem` keyed on the field. Use 409 `TypedResults.Problem` for state conflicts.
- **SQLite specifics.** `DateTimeOffset` is stored as binary through a global convention in `PrikBoardDbContext` so it can be sorted and compared. That format keeps only 0.1 ms precision, so use `AddMilliseconds`, not ticks, to separate timestamps. EF's SQLite table rebuilds run after every other operation in a migration, so dropping a table that a rebuilt table still references cascade-deletes rows. `ReplaceColumnsWithBuckets` rebuilds `Notes` in raw SQL for that reason. Text search uses `EF.Functions.Like` with escaped wildcards. Migrations run automatically on startup. JSON uses `JsonNumberHandling.Strict` to keep the OpenAPI integer types clean. Enums are serialized as strings (`NoteColor`).

### Tests (`src/PrikBoard.Api.Tests`)

The tests are xUnit integration tests over HTTP only. `PrikBoardApiFactory` (a `WebApplicationFactory<Program>`) gives each test class its own temp SQLite file (`Pooling=False` so the file can be deleted). Tests in a class share that database, so each test creates its own board and doesn't assume global state.

### Web (`src/web`)

- `src/api/types.ts` contains **hand-written** mirrors of the C# contracts, and `src/api/client.ts` has one method per operationId with the same name. They are not generated. When you add or change an endpoint or contract, update both, plus the endpoint table in `docs/api.md`.
- No state library and no UI kit. `board/BoardCanvas` runs every mutation through `run()`, which calls the API and then reloads the full board with `getBoard`. While a pointer gesture is in progress it renders the snapped position locally (`Gesture`). On drop it sends one request and keeps the gesture until the reload lands, so items don't jump back.
- Dark is the default theme. `:root` in `index.css` holds the dark tokens, and `:root[data-theme='light']` overrides them.
- The selected board lives in the URL hash (`#/boards/{id}`).
- Linting is oxlint (`.oxlintrc.json`), not ESLint. TypeScript is strict about unused locals and parameters, and `verbatimModuleSyntax` requires `import type` for type-only imports.

## Constraints

- There is no authentication yet. The API must not be exposed publicly (see `infra/README.md`). Nothing is deployed, and `infra/` contains only plans.
- Connection string key: `ConnectionStrings:PrikBoard` (env var `ConnectionStrings__PrikBoard`).
