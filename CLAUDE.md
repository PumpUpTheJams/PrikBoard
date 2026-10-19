# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

PrikBoard is a post-it board for small project management. It has a .NET 10 REST API (`src/PrikBoard.Api`) and a React 19 + Vite SPA (`src/web`). People in the browser and AI agents both use the same API, and the OpenAPI document at `/openapi/v1.json` is the contract between them. Background reading: `docs/architecture.md` (design and open decisions) and `docs/api.md` (endpoint table and conventions).

## Commands

```bash
docker compose up -d                              # PostgreSQL 17 on localhost:5432 (user/password/db: prikboard), data in volume postgres-data
dotnet run --project src/PrikBoard.Api            # API on http://localhost:5080; applies migrations on startup
dotnet test PrikBoard.slnx                        # all API tests (what CI runs); needs the compose database
dotnet test PrikBoard.slnx --filter "FullyQualifiedName~Moving_a_bucket_carries_its_notes_along"   # one test

cd src/web && npm install && npm run dev          # SPA on http://localhost:5173, proxies /api and /openapi to :5080
cd src/web && npm run lint                        # oxlint (CI)
cd src/web && npm run build                       # tsc -b type check + vite build (CI)

dotnet tool restore                               # pinned dotnet-ef from .config/dotnet-tools.json
dotnet ef migrations add <Name> --project src/PrikBoard.Api --output-dir Data/Migrations
```

No local Node here: run web checks in Docker, e.g. `sg docker -c 'docker run --rm -u "$(id -u):$(id -g)" -e npm_config_cache=/tmp/.npm -v "$PWD/src/web":/app -w /app node:22 sh -c "npm run lint && npm run build"'` (the `web-docker` launch config runs the dev server the same way).

`.claude/launch.json` defines `api` and `web` preview servers. The frontend has no test suite. Set `PRIKBOARD_API_URL` to point the Vite proxy at a different API.

## Architecture

Domain: a board is a freeform canvas. `Board 1──* Bucket` (named rectangles) and `Board 1──* Note` (200×120 px post-its at x/y), and each note has an optional `BucketId`. A note also has tags, assignees, a checklist, links, an `AgentWorking` flag and a creator (`CreatedById`, null for older notes). The lists are stored on the note as arrays or JSON columns (`PrimitiveCollection`/`OwnsMany(...).ToJson()`) and always replaced as whole lists. `Board 1──* BoardTag`: the board's tags. Notes store tag *names*; a new name on a note joins the board (`TagEndpoints.EnsureBoardTagsAsync`), and renaming or deleting a tag rewrites every note in one transaction. Assignees are principal ids (`Note.AssigneeIds`), shown with the principal's current name; only `BoardAccess.AssignableIds` (members plus their token agents that reach the board) can be added, but ids already on a note may stay. A new board gets the buckets *To do*, *Doing* and *Done* side by side (`Board.DefaultBuckets`). `Board 1──* BoardImage`: images on the canvas (x/y, display width, height from the aspect ratio) or attached to a note (`NoteId`, cascade-deleted with it); bytes live in `ImageContent` (`bytea`, own table, never loaded with the board).

### API (`src/PrikBoard.Api`)

- **Minimal APIs, not controllers.** There is one file per resource in `Endpoints/`, each exposing a `Map*Endpoints(this RouteGroupBuilder)` extension that `Program.cs` calls on the `/api` group. Handlers are private static methods that take `PrikBoardDbContext` directly; there is no service layer. They return `TypedResults` with `Results<...>` union return types, so OpenAPI lists the exact status codes.
- **Every endpoint needs `.WithName("camelCaseOperationId")`**, plus `WithSummary`/`WithDescription`. These names are the stable tool names for AI clients and generated clients. A test asserts that key operationIds exist (`OpenApi_document_exposes_named_operations_for_ai_clients`).
- **XML doc comments on `Contracts/` records become OpenAPI descriptions** (`GenerateDocumentationFile` is on, CS1591 is suppressed). Write them for an AI reader. Validation uses DataAnnotations on record properties (`[property: Required, StringLength(...)]`) through .NET 10's built-in `AddValidation()`. Errors are RFC 9457 problem+json.
- **Contracts are separate from entities.** Response records have a static `From(entity)` mapper. Don't return `Domain/` entities from endpoints.
- **The server owns the geometry** (`Domain/Layout.cs`). Coordinates snap to a 20 px grid (`Layout.Snap`). `Note.BucketId` is derived, never set directly: it is the topmost bucket containing the note's centre, with buckets stacked by `CreatedAt`. Every create, move, resize or delete of a note or bucket must call `Layout.AssignBuckets`. Moving a bucket shifts its notes by the same delta. createNote and moveNote take either `bucketId` (the server picks `Layout.MakeRoomIn`, which grows a full bucket downwards) or both `x` and `y`, never both. `src/web/src/board/layout.ts` mirrors these rules for drag feedback, so keep the two in sync.
- **REST and MCP parity.** Every feature works through both. Where REST takes ids (`bucketId`, `assigneeIds`, tag ids), MCP tools also take names and resolve them before calling the REST handler.
- **PATCH semantics.** A null field means unchanged. Optional text fields go through `PatchText.Apply`, where empty or whitespace clears the value (sets it to null). A list that is given replaces the whole list (`ApplyLists` in `NoteEndpoints`). Tags are normalized by `NoteTags.Normalize`, and links must be absolute http(s) URLs. Moving a note is a separate `POST /notes/{id}/move` (`moveNote`), not part of `updateNote`. Buckets move and resize through `updateBucket`.
- **Error shapes.** A bucket that isn't on the note's board, or an invalid `bucketId`/`x`/`y` combination, is a 400 `ValidationProblem` keyed on the field. Use 409 `TypedResults.Problem` for state conflicts.
- **Authentication** (`Auth/`, `docs/authentication.md`). Browsers sign in through a provider configured under `Auth:Providers` (generic OpenID Connect, plus a `GitHub` OAuth adapter) and get a cookie. `Auth:DefaultProvider` (github) is listed first. Machines send JWT access tokens (Client Credentials) from issuers under `Auth:Tokens:Issuers`; PrikBoard never issues tokens. The fallback policy `AuthSetup.ApiPolicy` requires a caller and, for tokens, scope `boards:read` (GET/HEAD) or `boards:write` (everything), so public endpoints need `.AllowAnonymous()`, which also clears their OpenAPI `security`. `ClaimsPrincipal.IsToken()` tells tokens from sessions, and `UserId()` only works for sessions. There is no dev login. `/scalar` (Scalar API reference) uses `.RedirectToSignIn()` to send anonymous visitors through the default provider. Keep `changeOrigin: false` on the Vite proxy (`/api`, `/openapi`, `/scalar`), because OAuth callback URLs are built from the Host header. External identities are mapped to a `User` (via a hashed `UserIdentity`) in `UserSignIn.OnTicketReceived`, which also handles linking a second sign-in. In that event `context.Fail()` does not stop the sign-in, so reject with `HandleResponse()` and a redirect. Boards are members-only (see below).
- **Users, members and personal data** (`Domain/Principal.cs`, `Auth/KeyedHasher.cs`, `Auth/BoardAccess.cs`):
  - Never store SSO names, emails or provider subject ids. A `User`/`Agent` (TPH `Principals`) is a GUID plus a self-chosen `DisplayName`.
  - `UserIdentity` keeps only `KeyedHasher` HMACs (key `Auth:HashKey`) of the subject and verified email.
  - Code that serves both people and tokens uses `PrincipalId()`. `UserId()` is for sessions only.
  - Token clients become `Agent`s in `OnTokenValidated` (`AgentRegistry`).
  - Boards are members-only (Owner/Editor/Viewer). **Every `/api` endpoint declares `.RequireBoardRole(role, BoardScope.X)` or `.NoBoardScope()`**; a test fails otherwise. The filter answers 404 to non-members and 403 to low roles, and handlers read the role with `HttpContext.BoardRole()`.
  - Membership changes lock the board row (`MemberEndpoints.LockBoard`) so a board always keeps an owner.
  - Email lookup, invite accept/preview and linking are sessions only.
  - Tests: `factory.CreateSignedInClientAsync(...)`, `TestLinkAsync`, and `TestApi` helpers (`MeAsync`, `CreateBoardAsync`, `ShareAsync`).
- **Personal access tokens and MCP**:
  - `Bearer pb_pat_...` goes to the `pat` scheme (`AccessTokenHandler`). The token acts as its own `Agent` (issuer `pat`), with its owner's memberships capped and limited by `BoardCaller` (`Auth/AccessTokens.cs`).
  - Use `BoardCaller.Of(principal)` or `BoardAccess.RoleOnBoardAsync(db, boardId, principal, ct)` for any new board-role lookup; never query `BoardMembers` by `PrincipalId()` directly.
  - Token management endpoints are sessions only.
  - `/mcp` (`Mcp/BoardTools.cs`, policy `AuthSetup.McpPolicy`: bearer only, Origin check) calls the `internal` REST handlers after `McpCall.AuthorizeAsync`, validates with `McpCall.Validate`, and must `McpCall.AnnounceAsync` after writes.
  - Tool errors are `McpException`s.
  - MCP tools need `JsonSerializerOptions` with a `TypeInfoResolver` (`McpCall.Json`).
  - Tests: `AccessTokenTests`, and `McpTests` with the official MCP client over the test server.
- **Live updates** (`Live/`, SignalR hub `BoardHub` at `/api/live`, policy `AuthSetup.LivePolicy`, which also checks the Origin of cookie connections):
  - The `RequireBoardRole` filter announces successful board-scoped writes (`BoardChanged`) itself. Mark board-scoped POSTs that change nothing with `.Quiet()`. Changes to board lists, roles or names call `BoardEvents` explicitly (`BoardsChangedAsync`, `AccessChangedAsync`, `RenamedAsync`).
  - Presence and roles of open connections live in the `LiveViewers` singleton (one instance only). SignalR user ids are principal ids (`PrincipalUserIds`).
  - The web app sends its connection id as `X-PrikBoard-Live` on changes, so it is not told about its own changes.
  - Tests use the SignalR client over long polling against the test server (`LiveApiTests`), with the session cookie and an `Origin` header.
- **PostgreSQL specifics** (Npgsql). `DateTimeOffset` maps to `timestamp with time zone`, which only accepts UTC offsets and keeps microsecond precision, so always use `DateTimeOffset.UtcNow` and use `AddMilliseconds`, not ticks, to separate timestamps. Note tags are a `text[]` column and assignee ids a `uuid[]`; checklist and links are `jsonb`. Text search uses `EF.Functions.ILike` (case-insensitive) with escaped wildcards. Npgsql's EF Core packages lag the Microsoft ones, so the API pins `Microsoft.EntityFrameworkCore.Relational` to the same version as `Microsoft.EntityFrameworkCore.Design`; without it the tests fail with CS1705. Migrations run automatically on startup. JSON uses `JsonNumberHandling.Strict` to keep the OpenAPI integer types clean. Enums are serialized as strings (`NoteColor` fills are Butter, Mint, Sky, Coral and Lilac).

### Tests (`src/PrikBoard.Api.Tests`)

The tests are xUnit integration tests over HTTP only. `PrikBoardApiFactory` (a `WebApplicationFactory<Program>`) gives each test class its own PostgreSQL database `prikboard_tests_<guid>` on the compose server (override the server with `PRIKBOARD_TEST_POSTGRES`), dropped in `DisposeAsync`. CI runs the same image as a service container. Tests run in a `Testing` environment (no user secrets) with the GitHub client id from appsettings.json cleared, so they start without sign-in providers; test factories add the providers they need with `UseSetting`. Tests in a class share that database, so each test creates its own board and doesn't assume global state. Every `/api` call needs auth: use `factory.CreateSignedInClientAsync(email)`, which signs in through a test-only middleware the factory adds (the API has no sign-in shortcut), or a JWT from the in-memory issuers in `TokenAuthTests`.

### Web (`src/web`)

- `src/api/types.ts` contains **hand-written** mirrors of the C# contracts, and `src/api/client.ts` has one method per operationId with the same name. They are not generated. When you add or change an endpoint or contract, update both, plus the endpoint table in `docs/api.md`.
- `App` checks `whoAmI` and shows `SignIn` until there is a session. Any 401 from `client.ts` goes back to sign-in (`setUnauthorizedHandler`). The signed-in user's initials, theme switch, API reference link and sign-out live in the settings menu on the right of the top bar.
- The design is the "Night shift" mockup (`docs/Scrum Board — Night shift.html`): Geist fonts, a floating tool rail, a zoom/minimap corner, frame-style buckets, post-its showing title, tags and assignees, and an expanded post-it view (`board/ExpandedNote`). Images are built (`board/BoardImageView`, `board/files.ts`); freehand drawing from the mockup is not.
- No state library and no UI kit. `board/useBoard` (used in `App`'s `Workspace`) loads the board and exposes `run()`, which calls the API and then reloads the full board. `live/useLive` keeps a SignalR connection: others' saves call `useBoard`'s `refresh()`, and polling drops from every 4 s to every 30 s while connected. Others' drags in progress come in as `remoteDrags` and are applied by the same geometry functions as your own gesture (`bucketRect`, `notePoint`, `imageRect`). The Vite proxy has `ws: true` for the hub. `BoardCanvas` wraps it in `settle()`. While a pointer gesture is in progress it renders the snapped position locally (`Gesture`) and pauses polling. On drop it sends one request and keeps the gesture until the reload lands, so items don't jump back. Every press on an item captures the pointer on the canvas, so the browser sends `click` and `dblclick` to the canvas, not the item: `BoardCanvas` works out clicks itself (`notePresses` for double-click to expand, `titlePress` for click-to-edit a title, with `caretAt` placing the text cursor).
- The canvas is scaled with a CSS transform for zoom. `toCanvas()` divides by the zoom, and the canvas sits `RAIL_INSET` px right of the scroll origin so the tool rail doesn't cover it.
- Dark is the default theme. `:root` in `index.css` holds the dark tokens, and `:root[data-theme='light']` overrides them. Post-it fills (`.fill-*`) and their ink are the same in both themes.
- Ask before destructive actions with `components/ConfirmDialog`, never `window.confirm`: embedded browsers such as the Claude desktop browser pane suppress native dialogs and answer "cancel", so the action silently never happens. A selected post-it or image is deleted with Delete/Backspace (after confirming), or a post-it with the red trash button in the expanded post-it's dock. Ctrl/Cmd+D duplicates the selected post-it one grid step down and right (content only, not attached images).
- Tags and people are picked in `components/PickerWindow` (via `board/TagPicker`, `board/AssigneePicker`), from the post-it on the board or from the expanded view. It is portalled outside the zoomed canvas and keeps focus in its field; the canvas key handler ignores `[role="dialog"]`. `board/tags.ts` mirrors `NoteTags.Normalize` and has `useDraftList`, which sends whole-list saves one after another.
- Images are uploaded as the raw request body (`api.uploadImage` sends the `File` with its own content type), never as a form: a form encoding would let another site post with the session cookie, so `uploadImage` rejects them. The server sniffs the real format (`ImageFormats.Detect`); SVG is refused.
- The selected board lives in the URL hash (`#/boards/{id}`).
- Linting is oxlint (`.oxlintrc.json`), not ESLint. TypeScript is strict about unused locals and parameters, and `verbatimModuleSyntax` requires `import type` for type-only imports.

## Constraints

- Nothing is deployed, and `infra/` contains only plans. Before deploying, persist the Data Protection keys and serve over HTTPS (`docs/authentication.md`).
- Connection string key: `ConnectionStrings:PrikBoard` (env var `ConnectionStrings__PrikBoard`).
