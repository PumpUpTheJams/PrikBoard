# Authentication

PrikBoard accepts two kinds of callers:

| Who | How | Scheme |
| --- | --- | --- |
| People in the browser | Sign in with an identity provider (GitHub by default), then hold an HttpOnly session cookie (`prikboard`, 14 days, sliding) | Cookie |
| Machines and other OAuth clients | An access token from a trusted issuer, sent as `Authorization: Bearer <token>`. Usually obtained with the **Client Credentials** grant | JWT bearer, one scheme per issuer |

PrikBoard does not issue tokens itself. An external identity provider (Auth0, Entra ID, Okta, Keycloak and so on) does, and PrikBoard validates them: signature, issuer, audience and expiry.

A policy scheme picks the scheme per request. A request with a `Bearer` header is validated as a token. When several issuers are configured, the token's `iss` claim picks the issuer's scheme, which then validates everything. Every other request is checked for the cookie. The fallback authorization policy (`AuthSetup.ApiPolicy`) applies to every endpoint that does not say `AllowAnonymous()`.

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

A user is identified by provider plus subject id, never by email, so the same email at two providers is two separate users.

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

By default anyone who can sign in with a configured provider gets in, and **all signed-in users see all boards**. To restrict browser sign-in, set either or both of:

```json
"Auth": { "AllowedEmails": ["ada@example.com"], "AllowedEmailDomains": ["example.com"] }
```

The check uses the email claim, and an email the provider marks `email_verified: false` does not count. Microsoft does not send `email_verified`, so with an allowlist prefer a tenant-specific Microsoft authority. A rejected sign-in returns to `/?authError=not_allowed`, and a failed provider round trip returns to `/?authError=failed`. The allowlist does not apply to machine tokens: control those through which clients your identity provider grants the scopes to.

## Local development

There is no development shortcut for signing in. Register a GitHub OAuth App for `http://localhost:5173` (see above) and put its id and secret in user secrets. The integration tests sign in through a test-only hook in `PrikBoardApiFactory` and get tokens from in-memory issuers in `TokenAuthTests`.

## Before deploying

- Persist the ASP.NET Core Data Protection keys (for example `AddDataProtection().PersistKeysToFileSystem(...)` on a volume, or Azure Key Vault/Blob). Otherwise every restart or extra instance invalidates all sessions and in-flight sign-ins.
- Serve over HTTPS. The OAuth correlation cookies need it, and so do most providers' callback URL rules for non-localhost hosts.
- Register production callback URLs (a separate GitHub OAuth App) and the token issuers.
