# Design: personal access tokens and the `/mcp` endpoint

Status: phases 1 and 2 built on branch `feature/access-tokens-mcp`, with option A (delegated agent). Phase 3 (OAuth for connectors) is not built.

Differences from the proposal below:
- Token hashes are plain SHA-256, like invite tokens, not HMAC: the token has 256 random bits, and plain SHA-256 keeps tokens working if `Auth:HashKey` changes.
- `MaxRole` also sets the scopes: Viewer tokens are read-only, Editor tokens can write.
- Revoking deletes the token but keeps its agent, so what it did keeps its name.
- No protected resource metadata yet; it comes with phase 3.

## Goal

Today an agent reaches PrikBoard only with an OAuth access token from a configured issuer (Client Credentials), and an owner must add that client to each board. That is right for organisations, but heavy for the common case: *"I want Claude Code (or Cursor, or a script) to work on my boards."*

We want:

1. **Personal access tokens (PATs):** a signed-in person creates a token in PrikBoard and pastes it into an agent. The token works for the REST API and for MCP.
2. **An MCP server at `/mcp`:** agents get curated tools (`get_board`, `move_card`, ...) instead of having to read the OpenAPI document.

Constraints we keep from earlier work:

- no personal data;
- GUID ids;
- members-only boards with Owner, Editor and Viewer roles;
- token scopes `boards:read` / `boards:write`;
- agents can't accept invites or look up emails.

## 1. Personal access tokens

### Who does a PAT act as?

This is the main decision. Three options:

| Option | How it works | For | Against |
| --- | --- | --- | --- |
| **A. Delegated agent (recommended)** | The token belongs to a person and acts as an **Agent principal** owned by that person, for example "Claude Code (Jimmy)". On a board, its role is the **lower of** the owner's role and the token's maximum role, optionally limited to chosen boards. | Works on all your boards at once, with no owner adding it per board. Changes still show as the agent, so people can tell Claude moved the card, not you. Losing access as a person also cuts off your tokens. | Your role caps it, so it can't do anything you can't. A board's owners can't component your delegated agent separately; they'd have to remove you. |
| B. Act as the user | The token is simply you, like a GitHub classic PAT. | Simplest. | Board history and presence can't tell you from your agent, and the agent can do everything you can, including managing members. |
| C. Standalone agent | The token mints an Agent that owners add per board, the way token issuers work today. | Owners keep full control. | Same friction as today; the "paste a token and go" story is lost. |

**Recommendation: A.** It keeps "agents are visible as agents" from the membership design and still removes the friction. Option C stays available through the existing issuer flow.

### Data model

New table **`AccessTokens`**:

| Column | Cards |
| --- | --- |
| `Id` | Guid v7 |
| `OwnerId` | FK to Users, cascade. Deleting the person deletes their tokens. |
| `AgentId` | FK to the `Agent` principal this token acts as, cascade. One agent per token, so each tool shows up separately. |
| `Name` | Chosen by the person, for example "Claude Code laptop". It is also the agent's display name. |
| `TokenHash` | `bytea(32)`, unique. HMAC-SHA256 with a new `KeyedHasher` purpose `prikboard/v1/pat`. Tokens are high-entropy, so no slow hash is needed. The key means a leaked database can't even be checked against a guessed token. |
| `Hint` | The last 4 characters, to tell tokens apart in the list (`pb_pat_…x7Qa`). |
| `Scopes` | `boards:read` or `boards:read boards:write`. |
| `MaxRole` | Viewer or Editor. Never Owner: an agent never manages members or deletes boards. |
| `BoardIds` | `uuid[]`, null means all of the owner's boards. |
| `CreatedAt`, `ExpiresAt` | `ExpiresAt` is required: 90 days by default, at most one year. |
| `LastUsedAt` | Updated at most once a minute per token. |

`Agent` gets a nullable `OwnerId`. The existing `(Issuer, ClientId)` agents keep it null. PAT agents use issuer `"pat"` and the token id as the client id, so the existing unique index still holds.

### Token format

`pb_pat_` + 43 characters of base64url (32 random bytes) + a 6-character CRC32 checksum, the way GitHub's tokens work.

- The **prefix** lets the API tell a PAT from a JWT without parsing it. It also lets secret scanners find leaked tokens; we can register the pattern with GitHub secret scanning later.
- The **checksum** rejects typos and garbage before any database lookup.
- The token is shown **once**, at creation.

### Authentication

- **A new `Pat` scheme** (`Auth/AccessTokenHandler.cs`, an `AuthenticationHandler`).
- `AuthSetup.SelectScheme` sends `Bearer pb_pat_…` to `Pat` before the issuer logic. Everything else is unchanged.
- **The handler:**
  1. Checks the format and checksum, and hashes the token.
  2. Looks it up through `IMemoryCache`, cached for 60 seconds and invalidated on revoke.
  3. Rejects expired tokens and tokens whose owner is gone.
  4. Builds a principal with the claims the rest of the API already understands:
     - `prikboard_issuer = "pat"`, so `IsToken()` is true and `ApiScopes` applies the scopes unchanged;
     - `scope`;
     - `prikboard_principal = AgentId`, so `PrincipalId()` is the agent;
     - new: `prikboard_owner = OwnerId`, `prikboard_max_role` and `prikboard_boards`.
- **`BoardAccess`:** the role lookup gains one branch. For a principal with `prikboard_owner`, the role is `min(owner's membership role, MaxRole)`, and only on the listed boards when `BoardIds` is set. Non-members still get 404 and low roles 403, so the guard test and every endpoint keep working without changes.
- **`listBoards`** for a delegated agent lists the owner's boards, filtered the same way. `createBoard` makes the **owner** the board's Owner, and the agent can then edit it.
- **The live hub** (`/api/live`) accepts PATs for free through the same scheme. The `LiveViewers` role comes from the same role lookup.
- **Rate limit:** a per-token fixed window, for example 600 requests a minute. Without one, a leaked token in a loop can hammer the database.

### Endpoints (sessions only: a token can never mint tokens)

| operationId | Route | |
| --- | --- | --- |
| `listAccessTokens` | `GET /api/me/tokens` | Name, hint, scopes, max role, boards, created, expires, last used. Never the token. |
| `createAccessToken` | `POST /api/me/tokens` | `{name, scopes, maxRole, boardIds?, expiresInDays?}`. Returns the token once. |
| `revokeAccessToken` | `DELETE /api/me/tokens/{id}` | Deletes the token and its agent. The cache entry is evicted at once. |

### Web

- A new section in the Account dialog: **Agents and access tokens**.
  - A list showing name, hint, access ("Editor on all boards"), expires and last used, with a Revoke button behind a confirm.
  - A **New token** form: name, access (read-only or can edit), boards (all or chosen), expiry.
- After creating a token, show it once with Copy, plus ready-to-paste snippets:

```bash
# Claude Code
claude mcp add --transport http prikboard https://<host>/mcp --header "Authorization: Bearer pb_pat_..."
# REST
curl -H "Authorization: Bearer pb_pat_..." https://<host>/api/boards
```

- Share dialog: delegated agents appear under their owner ("Claude Code · via Jimmy"). Owners see them, but can't change their role, because it follows the owner's role.

## 2. The `/mcp` endpoint

### Package and transport

- `ModelContextProtocol.AspNetCore` (2.2.0 at the time of writing) in the API project. There's no separate service: tools share the `DbContext`, `BoardAccess` and `BoardEvents`.
- **Streamable HTTP, stateless** (`SessionMode = HttpServerSessionMode.Stateless`, the SDK default). No `Mcp-Session-Id` and no per-client memory, so it scales like the REST API. We don't need server-initiated notifications yet; see "Later" for live resources.

```csharp
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "PrikBoard", Version = "1.0" })
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<BoardTools>()
    .WithTools<CardTools>();

app.MapMcp("/mcp").RequireAuthorization(AuthSetup.McpPolicy);
```

`/mcp` sits at the root, not under `/api`, because that's where MCP clients expect it. The Vite proxy and the nginx template both need a `/mcp` route. nginx must also not buffer SSE: `proxy_buffering off` on that location.

### Authentication on `/mcp`

- **Bearer only, never the cookie.** The `McpPolicy` lists only the token schemes (`Pat` plus the configured issuers) in `AuthenticationSchemes`, requires `boards:read`, and applies no method rule, because MCP POSTs even for reads.
  - Without the cookie, a page on another site can't drive `/mcp` with a signed-in person's session, which is the MCP equivalent of CSRF.
  - Write tools check `boards:write` themselves.
- **Origin:** the spec says servers MUST answer 403 to a request with an invalid `Origin` header. Agents send none, so: no Origin is allowed, our own origin is allowed, anything else gets 403. This reuses `SameOrigin`.
- **401 discovery:** MCP clients that do OAuth expect a 401 with `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource"`. They also expect that document (RFC 9728) to list an authorization server.
  - PrikBoard isn't one: PATs are issued by PrikBoard itself, and the external issuers are third parties.
  - So in phase 1 we publish the metadata only when a token issuer is configured, pointing at it. The SDK's `AddMcp` scheme does this.
  - Clients with a static header (Claude Code, Cursor, VS Code, scripts) never need it.
- **claude.ai and Claude Desktop custom connectors** need the OAuth flow, not a pasted header. That's phase 2 (see below).

### Tools

Hand-written and curated, rather than generated one per OpenAPI operation. Agents do much better with a few task-shaped tools that take names as well as ids. Each tool is a thin layer over the same logic as the REST handlers.

| Tool | Annotations | Does |
| --- | --- | --- |
| `list_boards` | read-only | Your boards with your role, the one you used last first |
| `get_board` | read-only | A compact snapshot: buckets in order, each with its cards (id, title, tags, assignee names, checklist progress, creator; collapsed buckets only counted), the board's tags and the people who can be assigned. Smaller than the REST `getBoard`, because agents pay per token. |
| `search_cards` | read-only | Like `listCards`: by bucket, tags, assignee, text |
| `get_card` | read-only | Full card: body, checklist, links, attached image captions |
| `create_card` | | Title, body, color, tags, assignees (**by name or id**), checklist, links, a bucket **by id or name**, and `working` to start on it at once; `create_cards` takes several |
| `update_card` | idempotent | PATCH semantics as in REST |
| `move_card` | idempotent | To a bucket by id or name. The server finds the spot. |
| `set_working` | idempotent | Assign yourself and set `agentWorking` on or off (REST: `setCardWorking`) |
| `delete_card` | **destructive** | |
| `create_bucket`, `update_bucket` | | Rare for agents, but cheap to add |
| `create_tag`, `update_tag`, `delete_tag` | `delete_tag` **destructive** | The board's tags by name or id; renaming and deleting change every card, and `update_tag` also changes the color |

- **No member, invite, board-delete or token tools.** Agents never manage access, and the tool list itself says so.
- **Annotations** (`ReadOnly`, `Destructive`, `Idempotent`) let clients ask before a destructive call.

### Sharing logic with REST

Today the handlers are private static methods, and the role check plus the live update happen in the `RequireBoardRole` endpoint filter. MCP calls don't go through endpoint filters, so:

1. Extract the filter's core into `BoardAccess.AuthorizeAsync(db, principal, scope, id, minRole)`, which returns the board id and role or a failure. The filter calls it, and so do the tools.
2. Make the handlers' work reusable: move it into `internal static` methods, for example `CardOperations.MoveAsync(db, cardId, request, ct)`, that both the endpoint and the tool call. Validation (DataAnnotations on the request records) runs through `Validator.TryValidateObject` in the tool layer.
3. Tools call `BoardEvents.ChangedAsync` after a successful write, so people watching the board see the agent's change live, exactly as with REST.
4. Tool errors go back as MCP tool errors (`isError` with the problem detail), never as exceptions with stack traces. 404 and 403 read the same as in REST: "not found, or you are not a member".

A tempting shortcut is an in-process HTTP loopback: the tool calls `/api/...` with the caller's token. That gets endpoint filters for free, at the cost of an extra HTTP round trip per call and awkward error mapping. It's fine as a first spike, but the extraction above is the version to keep.

### Prompt-injection hygiene

Card text is written by people and other agents, and it comes back in tool results.

- Tool descriptions say that card content is data, not instructions.
- Write tools are limited by the token's `MaxRole` (never Owner).
- Invites and email lookup stay unreachable for any token, as today.

## 3. Phases

1. **PATs**: table and migration, `KeyedHasher` purpose, scheme and handler, `BoardAccess` branch, the three endpoints, Account UI, docs, tests.
2. **`/mcp`** (read tools first, then write tools): package, `McpPolicy` with the Origin check, extraction of `AuthorizeAsync` and the shared operations, Vite and nginx routes, docs (`docs/api.md` gets an "MCP" section with client snippets), tests.
3. **OAuth for connectors (later)**: claude.ai custom connectors and other OAuth-only MCP clients need an authorization server.
   - Either: become a small OAuth server ourselves (authorization code + PKCE, Dynamic Client Registration or Client ID Metadata Documents, a consent screen, issuing short-lived tokens that are delegated agents, exactly like PATs).
   - Or: document using an external one (Auth0, Keycloak) through the existing issuers plus RFC 9728 metadata.
   - Being our own server is the better product; it's also the largest piece, so it goes last.

## 4. Tests

**PATs**
- A token is shown once and only its HMAC is stored.
- The prefix routes to the PAT scheme, and a bad checksum gives 401 without a database hit.
- Expired or revoked tokens give 401 at once (cache eviction).
- Role is `min(owner role, MaxRole)`.
- `BoardIds` restricts boards.
- An owner removed from a board takes their tokens with them.
- Scopes apply.
- Tokens can't create tokens, accept invites or look up emails.
- The rate limit kicks in.
- A token works on `/api/live`.

**MCP**, using `ModelContextProtocol` client over the test server's handler:
- `tools/list` shows exactly the curated set with annotations.
- Each tool matches its REST equivalent.
- The cookie alone gets 401.
- A foreign `Origin` gets 403.
- A read-only token can't call write tools.
- An agent's `move_card` raises `BoardChanged` for live viewers.
- Tool errors come back as `isError`, not protocol errors.

## 5. Open questions

- Should owners be able to opt a board out of delegated agents, as a board setting? Not in phase 1.
- Should PAT agents count as "here" in presence while they make calls? This could be done by remembering recent activity per agent for a minute or so.
- Should we register the `pb_pat_` pattern with GitHub secret scanning once the repository or product is public?

## References

- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), including [transports](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md) and the [ProtectedMcpServer sample](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/ProtectedMcpServer/Program.cs)
- [MCP transports, 2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports): Streamable HTTP and Origin validation
- [MCP authorization, 2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization): RFC 9728 protected resource metadata
