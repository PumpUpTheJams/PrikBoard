# PrikBoard for VS Code

Your PrikBoard boards, buckets and notes in the side bar. Work in progress.

On boards where you are an Editor or Owner you can, from the tree:

- add a note to a bucket (the **+** on the bucket row),
- rename a note, edit its tags, move it to another bucket, or delete it (right-click),
- tick or untick a checklist item (click it),
- toggle **Agent working** (the sparkle on the note row).

Viewers get a read-only tree. The server decides what is allowed and its message is shown if a change is refused.

## Use it

1. Start the API (`dotnet run --project src/PrikBoard.Api`).
2. Open this folder (`src/vscode`) in VS Code, run `npm install`, and press F5.
3. In the PrikBoard view, choose **Sign in**. Pick one of two ways to sign in.

**Through your identity provider (recommended).** The browser opens, you sign in, and the extension acts as *you*: your boards, your roles and your display name, which "my work" needs. Set it up once:

1. At your identity provider, register the extension as a public/native application that uses the authorization code flow with PKCE. Allow the redirect `http://127.0.0.1:<port>/callback` (Entra and Keycloak accept any loopback port; Auth0 needs one fixed port, so also set `prikboard.oidc.redirectPort`). Let it request the API's `boards:read` and `boards:write` scopes.
2. On the API, trust the issuer and list this application's client id, and use the same id for the issuer and your browser sign-in provider (see *Interactive apps acting for a person* in `docs/authentication.md`):

   ```json
   "Auth": { "Tokens": { "Issuers": { "auth0": {
     "Authority": "https://your-tenant.eu.auth0.com/",
     "UserClients": [ "<client id of the extension>" ]
   } } } }
   ```
3. In VS Code settings, fill in `prikboard.oidc.authority` and `prikboard.oidc.clientId` (and `prikboard.oidc.scopes` / `prikboard.oidc.audience` if your issuer names them differently). The extension keeps the tokens in VS Code's secret storage and renews them itself.

**With an access token.** **Sign in with an Access Token** takes a personal access token (PrikBoard: Account, Agents and access tokens). That token acts as its own agent with the board access you gave it, so it needs no identity provider setup, but PrikBoard does not know it is you.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `prikboard.serverUrl` | `http://localhost:5080` | Where the API runs |
| `prikboard.webUrl` | `http://localhost:5173` | Where the web app runs, for **Open in PrikBoard** |
| `prikboard.refreshSeconds` | 15 | How often the open view reloads |
| `prikboard.oidc.authority` | empty | Issuer URL of the identity provider that signs you in |
| `prikboard.oidc.clientId` | empty | Client id of the extension's application at that provider |
| `prikboard.oidc.scopes` | `openid offline_access boards:read boards:write` | Scopes to request, named as your issuer names them |
| `prikboard.oidc.audience` | `api://prikboard` | Sent as `audience` (Auth0 style); empty for issuers that take it from the scopes |
| `prikboard.oidc.redirectPort` | 0 | Fixed local port for the redirect, when the issuer needs the exact URI; 0 picks a free one |

## Code

The extension reuses the web app's API client and types (`src/web/src/api`) instead of keeping a copy. esbuild bundles them in, so those two files must stay free of DOM and React imports. `configureClient` points the client at a server and adds the bearer token.

```bash
npm run build      # type check and bundle to dist/
npm run package    # build a .vsix
```
