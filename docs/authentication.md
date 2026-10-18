# Authentication

PrikBoard accepts three kinds of callers:

| Who | How | Scheme |
| --- | --- | --- |
| People in the browser | Sign in with an identity provider (GitHub by default), then hold an HttpOnly session cookie (`prikboard`, 14 days, sliding) | Cookie |
| Agents acting for a person | A **personal access token** the person created in PrikBoard, sent as `Authorization: Bearer pb_pat_...` | `pat` (`Auth/AccessTokenHandler.cs`) |
| Machines and other OAuth clients | An access token from a trusted issuer, sent as `Authorization: Bearer <token>`. Usually obtained with the **Client Credentials** grant | JWT bearer, one scheme per issuer |

Apart from personal access tokens, PrikBoard does not issue tokens. An external identity provider (Auth0, Entra ID, Okta, Keycloak and so on) does, and PrikBoard validates them: signature, issuer, audience and expiry.

A policy scheme picks the scheme per request. A `Bearer` token starting with `pb_pat_` goes to the personal access token scheme. Any other request with a `Bearer` header is validated as an issuer token. When several issuers are configured, the token's `iss` claim picks the issuer's scheme, which then validates everything. Every other request is checked for the cookie. The fallback authorization policy (`AuthSetup.ApiPolicy`) applies to every endpoint that does not say `AllowAnonymous()`.

## Scopes

Tokens are limited by scope. Browser sessions are people and always have full access.

| Scope | Allows |
| --- | --- |
| `boards:read` | `GET` requests |
| `boards:write` | Everything, reads included |

A token without the scope it needs gets `403`. An invalid, expired, wrong-audience or untrusted token gets `401` with `WWW-Authenticate: Bearer`. Scopes are read from the `scope` claim (Auth0, Okta, Keycloak), `scp` (Entra delegated) or `roles` (Entra application permissions, which is what Client Credentials tokens from Entra carry). `GET /api/me` (`whoAmI`) shows a caller's scopes.

## Browser sign-in providers

Providers live under `Auth:Providers`, keyed by an id of lowercase letters, digits and dashes. `Auth:DefaultProvider` (`github`) is listed first and shown as the main button. The id appears in the URLs:

- Login: `/api/auth/login/{id}`
- Callback (register this as the redirect or callback URL): `{origin}/api/auth/callback/{id}`

In development the origin is the Vite server, `http://localhost:5173`. Vite proxies `/api`, `/openapi` and `/scalar` with `changeOrigin: false`, so the API sees `Host: localhost:5173` and builds the callback URL for 5173. Vite's string shorthand would turn `changeOrigin` on and break this.

A provider without a `ClientId` is skipped. `appsettings.json` holds templates for GitHub, Google and Microsoft, so enabling one only takes its client id and secret. Keep secrets out of the repository: use user secrets locally, and environment variables or a secret store when deployed (`Auth__Providers__github__ClientSecret`).

| Setting | Meaning |
| --- | --- |
| `Type` | `OpenIdConnect` (default) or `GitHub` |
| `DisplayName` | Label on the sign-in button |
| `Authority` | The OpenID Connect issuer URL. Its `/.well-known/openid-configuration` must exist. Not used for GitHub |
| `ClientId`, `ClientSecret` | From the provider's app registration |
| `Scopes` | Extra scopes on top of the defaults |

### GitHub (the default)

GitHub does not support OpenID Connect for signing users in, so it has its own `Type: "GitHub"` adapter: plain OAuth 2.0, with the scopes `read:user` and `user:email`. The user's email is their primary *verified* GitHub email.

1. Go to GitHub → **Settings** → **Developer settings** → **OAuth Apps** → **New OAuth App** (https://github.com/settings/applications/new). For an organisation-owned app, use the organisation's Settings → Developer settings instead.
2. Fill in:
   - **Application name:** for example `PrikBoard (local)`
   - **Homepage URL:** `http://localhost:5173`
   - **Authorization callback URL:** `http://localhost:5173/api/auth/callback/github`
3. Click **Register application**. The page shows the **Client ID**.
4. Click **Generate a new client secret** and copy it straight away, because GitHub only shows it once.
5. Store both as user secrets:

   ```bash
   dotnet user-secrets --project src/PrikBoard.Api set "Auth:Providers:github:ClientId" "<client id>"
   dotnet user-secrets --project src/PrikBoard.Api set "Auth:Providers:github:ClientSecret" "<client secret>"
   ```

6. Restart the API. The sign-in screen now shows **Continue with GitHub**.

A GitHub OAuth App has exactly one callback URL, so register a separate app for each environment (local, staging, production).

### Any OpenID Connect provider

```json
"Auth": {
  "Providers": {
    "auth0": { "DisplayName": "Auth0", "Authority": "https://your-tenant.eu.auth0.com/", "ClientId": "..." },
    "okta": { "DisplayName": "Okta", "Authority": "https://your-org.okta.com/oauth2/default", "ClientId": "..." },
    "keycloak": { "DisplayName": "Keycloak", "Authority": "https://sso.example.com/realms/main", "ClientId": "..." }
  }
}
```

**Google:** Google Cloud Console → APIs & Services → Credentials → Create OAuth client ID → Web application, with the redirect URI `.../api/auth/callback/google`.

**Microsoft:** Entra admin centre → App registrations → New registration, platform Web, redirect URI `.../api/auth/callback/microsoft`, then create a client secret. The template's `common` authority accepts any work, school or personal account. Use `organizations`, `consumers` or a tenant id to narrow that down.

## Users, linked sign-ins and personal data

PrikBoard stores **no personal data** about people: no name, no email and no raw provider id.

- A **user** is a GUID with an optional display name the person chooses (`PATCH /api/me`). Until they pick one, others see a fallback such as `User 1a2b`. The provider's name is offered once as a suggestion. It travels only in the encrypted session cookie and is never stored.
- Each **sign-in** a user has (GitHub, Google, ...) is a `UserIdentity`. It holds the provider id plus two keyed hashes:
  - an HMAC-SHA256 of the provider's subject id, used to recognise the person when they sign in again;
  - an HMAC-SHA256 of their verified email, used for exact-email search.
- **Email search** only finds people whose provider vouched for the email. That means GitHub's verified primary address, `email_verified: true`, or a provider with `EmailsAreVerified: true`. Microsoft does not send `email_verified`, so Microsoft users are not findable by default.
- **Linking.** A signed-in person adds another sign-in from Account → *Connect another*. Under the hood that is a form post to `POST /api/auth/link/{provider}`.
  - The form post only works from PrikBoard's own pages: the `Origin` must match, and the SameSite=Lax cookie is not sent on cross-site posts.
  - The user id travels in the encrypted OAuth state. On the way back it must match the session again, so a callback replayed in someone else's browser does nothing.
  - Accounts are never merged. A sign-in that already belongs to another user is refused (`?authError=identity_in_use`).
- **No linking by email.** Sign-ins are never joined just because their emails match: a provider with weak email checks could otherwise take over accounts.
- **Unlinking.** People remove sign-ins in the same place. The last one cannot be removed.

### Hash key (`Auth:HashKey`)

The hashes are keyed with a secret, so a leaked database alone reveals nothing. Generate it once, base64, at least 32 bytes:

```bash
openssl rand -base64 32
```

Set it with `dotnet user-secrets set "Auth:HashKey" "<key>" --project src/PrikBoard.Api`, or the environment variable `Auth__HashKey`. Outside Development the API refuses to start without it. Development and the tests fall back to a fixed, public development key.

**Losing or changing the key orphans every account.** People's next sign-in creates a new, empty user, and email search stops finding anyone. There is no key rotation yet.

## Boards, roles and agents

Boards are members-only. The roles are:

| Role | Can |
| --- | --- |
| **Viewer** | Read the board. |
| **Editor** | Also change buckets, notes and images. |
| **Owner** | Also rename or delete the board, and manage members and invite links. |

The creator becomes the owner, and a board always keeps at least one. Owners add people by exact email, or with a single-use invite link (`/#/invite/{token}`, 7 days by default). Only a hash of the invite token is stored.

Every machine token acts as an **agent** principal, keyed by issuer id and client id (azp, client_id or appid). An agent sees no boards until an owner adds it with *Share → Add an agent*. Agents become owners of the boards they create. Token scopes still apply on top of the role: a `boards:read` token cannot write even as an Editor. Agents cannot accept invite links or search people by email, so an agent that reads a link somewhere cannot use it to join a board.

## Personal access tokens

A signed-in person creates tokens under Account → *Agents and access tokens*, or with `POST /api/me/tokens` from a session. A token can't create tokens.

- **What it is.** `pb_pat_`, then 32 random bytes in base64url, then a 6-character checksum. The prefix routes it to the right scheme without a database lookup, and lets secret scanners spot leaked tokens. The checksum rejects typos before the lookup.
- **What is stored.** A SHA-256 of the token (it has 256 random bits, so no key or slow hash is needed), the last 4 characters as a hint, the access, the boards, and the expiry. The token is shown once.
- **Who it acts as.** Each token has its own `Agent` principal (issuer `pat`, client id = token id, `OwnerId` = the person). Changes and live updates show that agent.
  - Board access follows the **owner's** memberships, capped at the token's access (Viewer or Editor) and limited to its boards (`BoardCaller` in `Auth/AccessTokens.cs`, applied by `BoardAccess`).
  - Viewer tokens get only `boards:read`.
  - Tokens are refused, like issuer tokens, wherever a person is needed: invites, email lookup, linking sign-ins, managing tokens.
- **Lifetime.** 90 days by default, 1 to 365 days, at most 50 per person.
  - Lookups are cached for a minute. Revoking evicts the cache on the instance that handled it; on other instances, a revoked token works for up to a minute.
  - `lastUsedAt` is updated at most once a minute.
- **Rate limit.** 600 requests a minute per token (`RateLimits:AccessTokenRequestsPerMinute`).
- **The `/mcp` endpoint** accepts personal access tokens and issuer tokens, never the cookie (`AuthSetup.McpPolicy`). It also refuses requests whose `Origin` header names another site, as the MCP spec requires.

## Machine-to-machine: Client Credentials

Register PrikBoard as an API at your identity provider, with the scopes `boards:read` and `boards:write`. Then register each machine as a client that may request them. Trust the issuer under `Auth:Tokens`:

```json
"Auth": {
  "Tokens": {
    "Audience": "api://prikboard",
    "Issuers": {
      "auth0": { "Authority": "https://your-tenant.eu.auth0.com/" },
      "entra": { "Authority": "https://login.microsoftonline.com/<tenant id>/v2.0" }
    }
  }
}
```

| Setting | Meaning |
| --- | --- |
| `Tokens:Audience` | The `aud` tokens must carry. Default `api://prikboard` |
| `Issuers:{id}:Authority` | The issuer's base URL. Its discovery document supplies the signing keys. An issuer without one is skipped |
| `Issuers:{id}:Issuer` | The exact `iss` value, when it differs from the Authority |
| `Issuers:{id}:Audience` | Overrides `Tokens:Audience` for this issuer |

Provider notes:

- **Auth0:** APIs → Create API with identifier `api://prikboard`, and add the permissions `boards:read` and `boards:write`. Then Applications → Create → Machine to Machine, and authorise it for the API with the permissions it needs.
- **Entra ID:** In an app registration for PrikBoard, set the Application ID URI (use it as the `Audience`), and add the app roles `boards:read` and `boards:write` for Applications. Grant them to each client app with admin consent. Entra puts them in `roles`. Use a tenant-specific authority.
- **Keycloak/Okta:** Create client scopes `boards:read`/`boards:write` and enable the client credentials flow (Keycloak: service accounts) on each client. Make sure the audience claim contains the configured audience.

Then a machine gets a token and calls the API, for example against Auth0:

```bash
TOKEN=$(curl -s https://your-tenant.eu.auth0.com/oauth/token -H 'Content-Type: application/json' \
  -d '{"grant_type":"client_credentials","client_id":"...","client_secret":"...","audience":"api://prikboard"}' | jq -r .access_token)
curl localhost:5080/api/me -H "Authorization: Bearer $TOKEN"
```

Tokens a user obtains through an OAuth client (delegated, for example an agent acting for a person) are accepted the same way, provided they come from a trusted issuer with the right audience and scopes. They are treated as tokens (`kind: Token`), not as that person's browser session.

## API reference (Scalar)

`/scalar` serves the Scalar API reference. A visitor who is not signed in is redirected to the default provider (`Auth:DefaultProvider`, GitHub) and back, so "Test Request" calls carry the session cookie. With no provider configured, the page is shown anyway as read-only documentation. Scalar sends an empty `Authorization: Bearer` header while its token field is blank. The scheme selector treats that as no token, so the cookie still applies. Telemetry and Scalar's agent feature are turned off.

## Who may sign in

Anyone who can sign in with a configured provider gets in; there is no allowlist. They see only boards they are a member of, so enable only providers whose users you trust. A failed provider round trip returns to `/?authError=failed`.

## Local development

There is no development shortcut for signing in. Register a GitHub OAuth App for `http://localhost:5173` (see above) and put its id and secret in user secrets. Development uses a built-in hash key unless you set `Auth:HashKey`.

The integration tests:
- sign in and link sign-ins through test-only hooks in `PrikBoardApiFactory` (`/test/login`, `/test/link`);
- use a fixed `Auth:HashKey`;
- get tokens from in-memory issuers in `TokenAuthTests`.

## Before deploying

- Persist the ASP.NET Core Data Protection keys (for example `AddDataProtection().PersistKeysToFileSystem(...)` on a volume, or Azure Key Vault/Blob). Otherwise every restart or extra instance invalidates all sessions and in-flight sign-ins.
- Serve over HTTPS. The OAuth correlation cookies need it, and so do most providers' callback URL rules for non-localhost hosts.
- Register production callback URLs (a separate GitHub OAuth App) and the token issuers.
- Set `Auth:HashKey` (see [Hash key](#hash-key-authhashkey)). Back it up together with the database, and use the same value on every instance. Without it the API does not start. If you lose it, every account is orphaned.
