# Authentication

PrikBoard accepts three kinds of callers:

| Who | How | Scheme |
| --- | --- | --- |
| People in the browser | Sign in with an identity provider (GitHub by default), then hold an HttpOnly session cookie (`prikboard`, 14 days, sliding) | Cookie |
| Agents acting for a person | A **personal access token** the person created in PrikBoard, sent as `Authorization: Bearer pb_pat_...` | `pat` (`Auth/AccessTokenHandler.cs`) |
| The VS Code extension | A personal access token of kind `App`, from [App sign-in](#app-sign-in-vs-code-extension), acting as the person | `pat` |
| Machines and other OAuth clients | An access token from a trusted issuer, sent as `Authorization: Bearer <token>`. Usually obtained with the **Client Credentials** grant | JWT bearer, one scheme per issuer |

Apart from personal access tokens (including the extension's app tokens), PrikBoard does not issue tokens. An external identity provider (Auth0, Entra ID, Okta, Keycloak and so on) does, and PrikBoard validates them: signature, issuer, audience and expiry.

A policy scheme picks the scheme per request. A `Bearer` token starting with `pb_pat_` goes to the personal access token scheme. Any other request with a `Bearer` header is validated as an issuer token. When several issuers are configured, the token's `iss` claim picks the issuer's scheme, which then validates everything. Every other request is checked for the cookie. The fallback authorization policy (`AuthSetup.ApiPolicy`) applies to every endpoint that does not say `AllowAnonymous()`.

## Scopes

Tokens are limited by scope. Browser sessions are people and always have full access.

| Scope | Allows |
| --- | --- |
| `boards:read` | `GET` requests |
| `boards:write` | Everything, reads included |

A token without the scope it needs gets `403`. An invalid, expired, wrong-audience or untrusted token gets `401` with `WWW-Authenticate: Bearer`. Scopes are read from the `scope` claim (Auth0, Okta, Keycloak), `scp` (Entra delegated) or `roles` (Entra application permissions, which is what Client Credentials tokens from Entra carry). `GET /api/me` (`whoAmI`) shows a caller's scopes.

## Browser sign-in providers

Providers come from two places:

- **The setup** (the web app's first page on a new server, and later Admin → *Instance*; see [Setup](#setup-and-recovery)). It stores them in the database (`SignInProvider`, with the client secret encrypted by Data Protection) and adds or changes their authentication schemes while the app runs (`SignInProviders`), so no restart is needed.
- **Configuration**, under `Auth:Providers`, as below. These are read-only in the setup, and they win when both use the same id.

Each provider has an id of lowercase letters, digits and dashes, which never changes: it is part of every sign-in's stored hash. `Auth:DefaultProvider` names a configured provider to list first and show as the main button; otherwise the provider the setup marked as default goes first, else the first one added. The id appears in the URLs:

- Login: `/api/auth/login/{id}`
- Callback (register this as the redirect or callback URL): `{origin}/api/auth/callback/{id}`

The setup shows each provider's callback URL for this origin. In development the origin is the Vite server, `http://localhost:5173`. Vite proxies `/api`, `/openapi` and `/scalar` with `changeOrigin: false`, so the API sees `Host: localhost:5173` and builds the callback URL for 5173. Vite's string shorthand would turn `changeOrigin` on and break this.

A provider without a `ClientId` is skipped (except Steam, which has none). `appsettings.json` holds templates for GitHub and Google, so enabling one only takes its client id and secret. Keep secrets out of the repository: use user secrets locally, and environment variables or a secret store when deployed (`Auth__Providers__github__ClientSecret`).

| Setting | Meaning |
| --- | --- |
| `Type` | `OpenIdConnect` (default), `GitHub`, `Discord`, `Steam` or `Slack` |
| `DisplayName` | Label on the sign-in button |
| `Authority` | The OpenID Connect issuer URL. Its `/.well-known/openid-configuration` must exist. Not used for GitHub |
| `ClientId`, `ClientSecret` | From the provider's app registration. Steam has no client id; its `ClientSecret` is an optional Steam Web API key |
| `Scopes` | Extra scopes on top of the defaults |

### GitHub (the default)

The setup's **GitHub** button creates a GitHub App from a manifest: GitHub opens with the app filled in (its callback URL, and read access to email addresses, the only permission it needs), the person creates it there, and GitHub sends them back to `/api/setup/github-app/callback` with a code that PrikBoard trades for the app's client id and secret (`Auth/GitHubApps.cs`). The app is public, because only its owner could sign in with a private one; its private key is not kept. The manual steps below are for a GitHub OAuth App instead, from the setup's *GitHub OAuth App* form or from configuration.

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

**Microsoft Entra ID** works as any OpenID Connect provider (the setup's *Other*): an app registration with platform Web and the redirect URI `.../api/auth/callback/{id}`, a client secret, and the authority `https://login.microsoftonline.com/{common|organizations|consumers|tenant id}/v2.0`. Microsoft sends no `email_verified`, so tick *Trust its email addresses* only for a tenant you control.

### Discord

`Type: "Discord"`, OAuth 2.0 with the scopes `identify` and `email`. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application, add the redirect `.../api/auth/callback/{id}` under OAuth2 and copy its client id and secret. The email counts as verified when Discord says so (`verified`); the name is the display name, else the username.

### Slack

`Type: "Slack"`: *Sign in with Slack*, Slack's OpenID Connect, with the scopes `openid`, `profile` and `email`. It runs through the OAuth handler with Slack's endpoints set (`slack.com/openid/connect/authorize`, `openid.connect.token`, `openid.connect.userInfo`), like Discord, so there is no discovery. Use any Slack app's client id and secret (*Basic Information → App Credentials*), such as the one the Slack integration runs as, and add `https://…/api/auth/callback/{id}` under *OAuth & Permissions → Redirect URLs*. Slack only takes https redirect URLs, so this needs PrikBoard's https address. The Slack app's manifest already lists the redirect for the id `slack`, and the user scopes.

The subject is `{team id}:{user id}` (`AuthSetup.SlackSubject`), because Slack user ids are only unique within their workspace. It is the same key the Slack app knows people by, so someone who signs in with Slack is known to the app without linking an account ([slack.md](slack.md)). The email counts as verified when Slack says so (`email_verified`).

### Steam

`Type: "Steam"`, OpenID 2.0 (the `AspNet.Security.OpenId.Steam` package; Steam's endpoint is set, so no discovery page is fetched). Steam needs no app registration and has no client id. Without anything else people sign in with their SteamID only and choose a name; with a [Steam Web API key](https://steamcommunity.com/dev/apikey) as the client secret, PrikBoard also gets their Steam name and picture. Steam never gives an email address, so Steam users can't be found by email.

## Users, linked sign-ins and personal data

PrikBoard stores **no personal data** about people beyond a copy of their GitHub picture (which they can remove): no name, no email and no raw provider id.

- A **user** is a GUID with an optional display name the person chooses (`PATCH /api/me`). Until they pick one, others see a fallback such as `User 1a2b`. The provider's name is offered once as a suggestion. It travels only in the encrypted session cookie and is never stored.
- **Avatars** are opt-out. Every sign-in downloads the provider's picture of the person (its `picture` claim: GitHub, Google, Discord, Steam with a Web API key, or any OpenID Connect provider that sends one; at most 1 MB, PNG, JPEG, GIF or WebP) into a file of its own (`User.AvatarFile`, see [Architecture](architecture.md)), replacing the copy when it changed. Only https URLs by host name are used (`ProviderAvatars`), and the download connects only to public addresses, also after a redirect, so a picture URL can't reach the server's own network. A failed download never stops a sign-in. The URL is never stored, because it contains the provider's account id; it only travels in the session cookie (`suggestedAvatarUrl` in `whoAmI`). Other people's browsers load the copy from `GET /api/avatars/{id}` without ever contacting the provider. Removing the picture (Account → *Picture*, `DELETE /api/me/avatar`) deletes the copy (its file goes at the next hourly sweep) and sets `User.AvatarOptedOut`, so later sign-ins leave it off until the person takes it again (`PUT /api/me/avatar`). Existing users get their picture on their next sign-in.
- Each **sign-in** a user has (GitHub, Google, ...) is a `UserIdentity`. It holds the provider id plus two keyed hashes:
  - an HMAC-SHA256 of the provider's subject id, used to recognise the person when they sign in again;
  - an HMAC-SHA256 of their verified email, used for exact-email search.
- **Email search** only finds people whose provider vouched for the email. That means GitHub's verified primary address, `email_verified: true`, or a provider with `EmailsAreVerified: true`. Microsoft does not send `email_verified`, so Microsoft users are not findable by default, and Steam sends no email at all.
- **Linking.** A signed-in person adds another sign-in from Account → *Connect another*. Under the hood that is a form post to `POST /api/auth/link/{provider}`.
  - The form post only works from PrikBoard's own pages: the `Origin` must match, and the SameSite=Lax cookie is not sent on cross-site posts.
  - The user id travels in the encrypted OAuth state. On the way back it must match the session again, so a callback replayed in someone else's browser does nothing.
  - OpenID Connect providers answer with a redirect (`response_mode=query`), not the usual `form_post`: the cross-site post back would not carry the SameSite=Lax session cookie, so the link could never find the session.
  - A failed link comes back as `?authError=…` while the person is still signed in, and the web app shows it over the board.
  - Accounts are never merged. A sign-in that already belongs to another user is refused (`?authError=identity_in_use`).
- **No linking by email.** Sign-ins are never joined just because their emails match: a provider with weak email checks could otherwise take over accounts.
- **Unlinking.** People remove sign-ins in the same place. The last one cannot be removed.

### Hash key (`Auth:HashKey`)

The hashes are keyed with a secret, so a leaked database alone reveals nothing. Without `Auth:HashKey`, PrikBoard generates one on its first start outside Development into the data folder (`{Storage:Path}/hash-key`, `KeyedHasher.KeyFileName`) and uses that file from then on. Admins download it in the **recovery kit** (getRecoveryKit; the setup offers it right after the first sign-in). To set your own instead, generate one, base64, at least 32 bytes:

```bash
openssl rand -base64 32
```

Set it with `dotnet user-secrets set "Auth:HashKey" "<key>" --project src/PrikBoard.Api`, or the environment variable `Auth__HashKey`. A configured key wins over the file (a different file only logs a warning). Development and the tests fall back to a fixed, public development key.

**Losing or changing the key orphans every account.** Nobody's sign-in is recognised any more, so people are turned away as newcomers (they would need a new invite link and get a new, empty user), and email search stops finding anyone. There is no key rotation yet.

## Boards, roles and agents

Boards are members-only. The roles are:

| Role | Can |
| --- | --- |
| **Viewer** | Read the board. |
| **Editor** | Also change buckets, cards and images. |
| **Owner** | Also rename or delete the board, and manage members and invite links. |

The creator becomes the owner, and a board always keeps at least one. Owners add people with a single-use invite link (`/#/invite/{token}`, 7 days by default) in the Share dialog, or through the API by exact email (findUserByEmail, then addBoardMember). Only a hash of the invite token is stored.

### Public boards

An owner can make a board public in the Share dialog (`updateBoard` with `isPublic`). Then anyone with its link views it as a **visitor**, without an account: someone signed out, or signed in but not a member. Visitors read like a Viewer, but only what endpoints marked `AllowVisitors()` serve: the board (getBoard, getBoardByShortId), its images and drawings, and the avatars of people on it (getBoardAvatar). They don't get the trash, an export, members or anything that changes the board, and the board doesn't appear in their board list.

- **Who.** These endpoints use `AuthSetup.PublicReadPolicy` instead of the fallback policy: no credentials at all, or a signed-in caller with read scope. A token that doesn't sign in (wrong, expired, revoked) still gets 401. `BoardAccess` falls back to the visitor check only when there is no membership, and **never for tokens**: agents and the VS Code extension still need membership, so a public board isn't readable over MCP by strangers. A private board still answers 404 to anyone without access, signed in or not.
- **Personal data.** The owner chooses whether visitors see who is on the board (`publicShowsPeople`, off by default). Hidden, `BoardDetail.ForVisitors` returns assignees as anonymous entries (empty id and name, the web app shows a blank circle), no creators, and no one who added an image or drawing; `people` is always empty for visitors. Shown, the names stay and pictures are served through getBoardAvatar, which only serves people on that board and, to visitors, only while the board shows people.
- **Live.** The hub admits connections without a cookie from PrikBoard's own pages (the same-origin check still applies). A visitor joins the board's visitors group: it hears that the board changed (without who changed it) and who is there, never drags. Members see the visitors as a guest count; visitors see the members by name when the board shows people, or else only a count. Making a board members-only, or changing what visitors see, sends visitors `AccessChanged`, so they look again (and lose the board).

Every machine token acts as an **agent** principal, keyed by issuer id and client id (azp, client_id or appid). The exception is a client the issuer lists under `UserClients`, see [Interactive apps acting for a person](#interactive-apps-acting-for-a-person-vs-code-extension). An agent sees no boards until an owner adds it through the API (addBoardAgent). Agents become owners of the boards they create. Token scopes still apply on top of the role: a `boards:read` token cannot write even as an Editor. Agents cannot accept invite links or search people by email, so an agent that reads a link somewhere cannot use it to join a board.

## Personal access tokens

A signed-in person creates tokens under Account → *Agents and access tokens*, or with `POST /api/me/tokens` from a session. A token can't create tokens.

- **What it is.** `pb_pat_`, then 32 random bytes in base64url, then a 6-character checksum. The prefix routes it to the right scheme without a database lookup, and lets secret scanners spot leaked tokens. The checksum rejects typos before the lookup.
- **What is stored.** A SHA-256 of the token (it has 256 random bits, so no key or slow hash is needed), the last 4 characters as a hint, the access, the boards, and the expiry. The token is shown once.
- **Who it acts as.** Each token has its own `Agent` principal (issuer `pat`, client id = token id, `OwnerId` = the person). Changes and live updates show that agent.
  - Board access follows the **owner's** memberships, capped at the token's access (Viewer or Editor) and limited to its boards (`BoardCaller` in `Auth/AccessTokens.cs`, applied by `BoardAccess`).
  - Viewer tokens get only `boards:read`.
  - Tokens are refused, like issuer tokens, wherever a person is needed: invites, email lookup, linking sign-ins, managing tokens.
- **Lifetime.** 90 days by default, 1 to 365 days, at most 50 per person.
  - Tokens found are cached for a minute. Revoking evicts the cache on the instance that handled it; on other instances, a revoked token works for up to a minute.
  - `lastUsedAt` is updated at most once a minute.
- **Rate limits.**
  - 600 requests a minute per token (`RateLimits:AccessTokenRequestsPerMinute`).
  - Unknown tokens (made up, revoked, or a typo the checksum missed) are not cached, as anyone can make up well-formed ones (`Auth/AccessTokenLookups.cs`). One address may present 30 a minute (`RateLimits:UnknownAccessTokensPerMinute`). After that, every token that isn't cached gets 429 with `Retry-After` until the minute is over. Tokens already looked up keep working.
  - The address is the client's as the API sees it: behind a reverse proxy (Caddy in the compose file), from `X-Forwarded-For`. An IPv6 address counts by its /64 network.
- **The `/mcp` endpoint** accepts personal access tokens and issuer tokens, never the cookie (`AuthSetup.McpPolicy`). It also refuses requests whose `Origin` header names another site, as the MCP spec requires.

### App sign-in (VS Code extension)

The extension's **Sign in** works like Claude Code's: PrikBoard opens in the browser, the person allows it, and the extension gets a personal access token of kind `App` that acts as **the person** (`whoAmI`: `kind: Token`, `principalKind: User`), with their own roles, Owner included. It needs no identity provider setup. It is an authorization code flow with PKCE in which PrikBoard is the authorization server (`Endpoints/AppSignInEndpoints.cs`):

1. The extension keeps a random PKCE verifier and `state`, listens on `http://127.0.0.1:<port>/callback` (through `asExternalUri`, so Remote SSH forwards it), and opens `{webUrl}/#/connect?state=…&code_challenge=…&name=…&redirect_uri=…`. It leaves out `redirect_uri` when the browser cannot reach it (Codespaces), and on **Paste a Code**.
2. The connect page (`components/ConnectPage.tsx`) asks the signed-in person to allow it, then calls approveAppSignIn. The server only takes this from a session on PrikBoard's own pages (`SameOrigin`), only a loopback `redirectUri` (`http`, `127.0.0.1`/`localhost`/`[::1]`, explicit port), and at most 5 waiting codes per person. It stores a SHA-256 of a 256-bit code with the challenge, for 5 minutes.
3. The page redirects to `redirect_uri?code=…&state=…`, or shows the code to paste with a warning never to send it to anyone.
4. The extension trades code and verifier with exchangeAppSignIn (anonymous, 10 a minute per IP). Any attempt spends the code; a wrong verifier, an expired or a used code all get `400`. The token lasts 90 days.

The code alone is useless, so one leaked through browser history or picked up by another program on the machine cannot be spent. The listener checks `state`, and a pasted code from someone else fails the verifier, so nobody can sign the extension in as another person. What remains is phishing: someone who gets a person to open their link in paste mode and send them the code gets a token. The page warns about exactly that, and the token shows up (with its device name and last use) under Account → *Agents and access tokens*, where the person can revoke it.

An App token is still a token: like any other it cannot manage tokens, approve app sign-ins, accept invites, look people up or link sign-ins. The web app refuses to be framed (`X-Frame-Options: DENY`, `frame-ancestors 'none'` on the API's static files and in the Vite dev server), so the Allow button cannot be clickjacked.

### The Discord bot

The Discord bot that previews pasted links ([discord.md](discord.md)) runs inside the API and calls no endpoint, so it is no caller here. Its token is a Discord bot token that an admin saves under Admin → *Instance*. It is stored encrypted with Data Protection, like sign-in provider secrets, or set as `Discord:BotToken` in the configuration.

Each connected Discord server is an `Agent` (issuer `discord`, client id the guild id) and a **Viewer** member of its boards. The bot looks links up as that agent. Board owners connect servers in the board's *Integrations*, from a browser session.

### The Slack app

The Slack app ([slack.md](slack.md)) also runs inside the API and calls no endpoint. Its bot and app-level tokens are saved by an admin under Admin → *Instance*, encrypted with Data Protection, or set as `Slack:BotToken` and `Slack:AppToken`. Each connected workspace is an `Agent` (issuer `slack`, client id the team id) and a **Viewer** member of its boards.

### Behind a proxy or tunnel

Callback URLs are built from the request's scheme and host. Behind a proxy that ends HTTPS (the Docker image behind Caddy, or a tunnel such as `cloudflared` in development), the API must trust `X-Forwarded-Proto`, or it builds `http://` callbacks that providers refuse. The Docker image sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. In development, the `api-tunnel` launch config does the same.

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
| `Issuers:{id}:UserClients` | Client ids of interactive apps (for example the VS Code extension) whose tokens act as the signed-in person. See below. Default none |
| `Issuers:{id}:UserProvider` | The sign-in provider id the token's `sub` belongs to. Default: the issuer id |

Provider cards:

- **Auth0:** APIs → Create API with identifier `api://prikboard`, and add the permissions `boards:read` and `boards:write`. Then Applications → Create → Machine to Machine, and authorise it for the API with the permissions it needs.
- **Entra ID:** In an app registration for PrikBoard, set the Application ID URI (use it as the `Audience`), and add the app roles `boards:read` and `boards:write` for Applications. Grant them to each client app with admin consent. Entra puts them in `roles`. Use a tenant-specific authority.
- **Keycloak/Okta:** Create client scopes `boards:read`/`boards:write` and enable the client credentials flow (Keycloak: service accounts) on each client. Make sure the audience claim contains the configured audience.

Then a machine gets a token and calls the API, for example against Auth0:

```bash
TOKEN=$(curl -s https://your-tenant.eu.auth0.com/oauth/token -H 'Content-Type: application/json' \
  -d '{"grant_type":"client_credentials","client_id":"...","client_secret":"...","audience":"api://prikboard"}' | jq -r .access_token)
curl localhost:5080/api/me -H "Authorization: Bearer $TOKEN"
```

## Interactive apps acting for a person (VS Code extension)

An app that signs a person in itself, such as the VS Code extension (authorization code with PKCE), gets a delegated access token from the same trusted issuer. By default PrikBoard treats it like any other token: the client becomes one shared **agent**, and the person is nobody. To make a token act as the person instead, list the app's client id on the issuer:

```json
"Issuers": {
  "auth0": {
    "Authority": "https://your-tenant.eu.auth0.com/",
    "UserClients": [ "<client id of the extension's application>" ]
  }
}
```

For a token whose client (azp, client_id or appid) is listed and that has a `sub`, PrikBoard finds the user for `UserProvider` (default: the issuer id) and that `sub`, with the same keyed hash as a browser sign-in. For the person to be the *same* user as in the browser, give the issuer and the sign-in provider the same id and the same identity provider (Auth0 as both `Providers:auth0` and `Tokens:Issuers:auth0`), or set `UserProvider` to the provider's id. Otherwise the token finds no user. Tokens never create users (see [Who may sign in](#who-may-sign-in)), so a person who has not joined in the browser yet gets `401`.

- `whoAmI` shows `kind: Token` and `principalKind: User`. Boards, roles, display name and memberships are the person's. Scopes from the token still apply on top of the role.
- Sign-ins are still managed in the browser. A token cannot link or unlink sign-ins, accept invite links or look people up by email.
- The VS Code extension can be such an app, when `prikboard.oidc.*` is set (see `src/vscode/README.md`). Without it, the extension uses [App sign-in](#app-sign-in-vs-code-extension), which needs none of this.
- Only list clients you control. Any token from a listed client with a valid `sub` acts as that person, so the client must authenticate the person properly. Public clients (no secret) rely on PKCE.
- Register the app at the identity provider as a public/native client with PKCE, a redirect URI of the extension (for VS Code, `http://127.0.0.1:<port>` or `vscode://...`), and the `boards:read`/`boards:write` scopes for the audience.

All other tokens, including Client Credentials ones, keep acting as agents. A delegated token from a client that is not listed is treated the same way (`kind: Token`, an agent keyed by the client id).

## API reference (Scalar)

`/scalar` serves the Scalar API reference. A visitor who is not signed in is redirected to the default provider (`Auth:DefaultProvider`, GitHub) and back, so "Test Request" calls carry the session cookie. With no provider configured, the page is shown anyway as read-only documentation. Scalar sends an empty `Authorization: Bearer` header while its token field is blank. The scheme selector treats that as no token, so the cookie still applies. Telemetry and Scalar's agent feature are turned off.

## Who may sign in

PrikBoard is **invite-only**. Signing in at a provider is not enough: a person only gets an account when they arrive through a board invite link.

- **The very first user** needs the **setup key** instead, and becomes the instance's **admin** (see [Setup](#setup-and-recovery)). Without it the callback turns them away (`/?authError=setup_required`), so a stranger can't take a new server by signing in before its owner. Sign-ups are serialised with a PostgreSQL advisory lock, so two first sign-ins at once cannot both become admin. (Upgrading an existing database makes its oldest user the admin.)
- **Newcomers** open an invite link (`/#/invite/{token}`) while signed out. Signing in carries the link in its return URL, which travels in the encrypted OAuth state, and the callback creates their user only if the token is an invite that is neither used, revoked nor expired. They then accept it on the invite page as usual, and from then on they can create boards of their own.
- **Anyone else** is turned away at the callback (`/?authError=invite_required`), and nothing about them is stored.
- **People who already have an account** sign in as before, with or without a link.
- **Delegated tokens** (the VS Code extension) never create users: a `sub` without a user gets `401` until the person has joined in the browser.

A failed provider round trip returns to `/?authError=failed`.

### Admins

Admins (`User.IsAdmin`) run the instance from *Admin* in the settings menu: they see every board with its members (not its content) and every user, give anyone any role on any board, take people off boards, delete any board, make others admin and delete users. The rules:

- There is always at least one admin (`409` otherwise), and an admin cannot delete themselves.
- A user who is the only owner of a board cannot be deleted until that board has another owner (`409`, with the boards in the problem's `boards` field).
- Deleting a user deletes their sign-ins, memberships and access tokens. Their cards and images stay (the creator becomes unknown). Their session cookie stops working on the next request: the cookie handler checks that the user still exists (`UserState`, cached for a minute and evicted on delete).
- The admin endpoints (`/api/admin/*`) are **sessions only**, like token management: tokens get `403`, and there are no MCP tools for them.

## Setup and recovery

A new PrikBoard is set up in the browser (`Endpoints/SetupEndpoints.cs`, `components/SetupPage`):

1. On a start without users, PrikBoard prints a **setup key** in its log (`SetupKey`: 20 Crockford base32 characters, new on every start, never stored; `docker compose logs app`). The web app shows the setup instead of signing in (getSetupStatus).
2. Entering the key (unlockSetup, 10 tries a minute per address) sets a setup cookie (`prikboard-setup`, an hour, `SetupKey.Scheme`) for that key.
3. With it, the browser manages the sign-in providers (listSignInProviders, createSignInProvider, updateSignInProvider, deleteSignInProvider, beginGitHubApp, checkOpenIdAuthority) and tries them: a **test sign-in** (`/api/setup/providers/{id}/test`) goes through the provider and back with `?setupTest=ok` or `failed` and the provider's reason, and signs nobody in (`UserSignIn.TestItem`).
4. The first sign-in in that browser becomes the admin, which closes the key.
5. The admin runs the **system checks** (getSystemChecks: database and migrations, the data folder, the Data Protection keys, the hash key, HTTPS, the providers and the version), downloads the **recovery kit** (getRecoveryKit: the hash key and how to restore a backup) and may create an access token for an agent.

Admins use the same endpoints later from Admin → *Instance*; a session that is an admin's counts like the setup cookie. Tokens get `403` everywhere here, and there are no MCP tools for any of it.

**Recovery.** If nobody can sign in any more (a provider removed or broken, its secret expired, or the Data Protection keys lost so stored secrets can't be decrypted), start PrikBoard once with `Setup__Recovery=true`. It prints a setup key even though users exist; with it the browser manages providers and runs the checks, but nobody becomes admin through it. Add the provider again under the **same id**: GitHub's user ids and Google's subjects stay the same for a new app, so people find their accounts again. Microsoft's `sub` is different for every app registration, so a new Microsoft app does not find the old accounts. Remove the setting and restart afterwards.

## Local development

There is no development shortcut for signing in. Register a GitHub OAuth App for `http://localhost:5173` (see above) and put its id and secret in user secrets. Development uses a built-in hash key unless you set `Auth:HashKey`.

The integration tests:
- sign in and link sign-ins through test-only hooks in `PrikBoardApiFactory` (`/test/login`, `/test/link`);
- use a fixed `Auth:HashKey`;
- get tokens from in-memory issuers in `TokenAuthTests`.

## Before deploying

The compose file in `infra/compose` covers most of this; see [`infra/README.md`](../infra/README.md).

- Keep the data folder (`Storage:Path`, `/data` in the image) on a volume and back it up with the database. It holds the images, the Data Protection keys (`keys/`, or `DataProtection:KeysPath`: sessions, sign-ins in progress and stored provider secrets) and the generated hash key. The keys are stored unencrypted on purpose, like the hash key next to them: the data folder's permissions protect both, so keep it readable by PrikBoard only, and anyone who has a copy of it can read sessions and provider secrets. To encrypt them anyway, configure Data Protection with a certificate of your own (a code change), and back the certificate up too. Run one instance only: live presence and the sign-in provider schemes are kept in memory.
- Download the recovery kit and keep it offline. If you lose the hash key, every account is orphaned.
- Serve over HTTPS. The OAuth correlation cookies need it, and so do most providers' callback URL rules for non-localhost hosts. The API trusts `X-Forwarded-*` headers, so it must sit behind the proxy that sets them, or listen on localhost only, as the compose file does.
- Register callback URLs for the server's own address (a new GitHub App from the setup does this) and the token issuers.
- Make sure the API sees each client's own address when there is another proxy in front of it. Otherwise all clients share one address, and so one limit for anonymous callers and for unknown access tokens (see [Personal access tokens](#personal-access-tokens)).
