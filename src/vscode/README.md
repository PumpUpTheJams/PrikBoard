# PrikBoard for VS Code

Your PrikBoard boards, buckets and cards in the side bar. Work in progress.

**Assigned to you**, at the top of the tree, lists the cards assigned to you on all your boards, sorted into their buckets (buckets with the same name on different boards, such as *Doing*, share one row), with the board next to each card. Collapse the buckets you care less about; VS Code remembers which ones. It uses who you are signed in as (`whoAmI`), so it shows your own cards after **Sign in**, or the agent's after signing in with an agent's access token.

On boards where you are an Editor or Owner you can, from the tree:

- add a card to a bucket (the **+** on the bucket row),
- rename a card, edit its tags, move it to another bucket, or delete it (right-click),
- tick or untick a checklist item (click it),
- toggle **Agent working** (the sparkle on the card row).

On every board, **Open in PrikBoard** (the arrow on board and card rows) opens the board or the card itself in the browser. Clicking a link to another card shows that card in the tree (under **Assigned to you** when you clicked it there and it is yours); a card that isn't on your boards opens in the browser instead, and so does the arrow on the link row.

Viewers get a read-only tree. The server decides what is allowed and its message is shown if a change is refused.

## Use it

1. Start the API (`dotnet run --project src/PrikBoard.Api`).
2. Open this folder (`src/vscode`) in VS Code, run `npm install`, and press F5.
3. In the PrikBoard view, choose **Sign in**. The first time, VS Code asks for your PrikBoard address: the one you open in the browser, for example `https://prikboard.example.com`, or `http://localhost:5173` for local development (Vite proxies the API). **PrikBoard: Set PrikBoard Address** (also in the view's `…` menu) changes it later and signs you out of the old server.

**Through PrikBoard (the default).** Like Claude Code: PrikBoard opens in your browser (sign in there if needed), you choose **Allow**, and the browser hands VS Code a token that acts as *you*: your boards, your roles and your display name, which "my work" needs. Nothing to set up beyond `prikboard.webUrl`. If the browser can't reach VS Code (another machine, Codespaces), choose **Paste a Code** and paste the code PrikBoard shows. The token lasts 90 days and is listed in PrikBoard under Account, Agents and access tokens, where you can revoke it. How it works: *App sign-in* in `docs/authentication.md`.

**Through your identity provider.** Instead of PrikBoard's own sign-in, the extension can sign in with an external identity provider, which then also acts as you. Set it up once:

1. At your identity provider, register the extension as a public/native application that uses the authorization code flow with PKCE. Allow the redirect `http://127.0.0.1:<port>/callback` (Entra and Keycloak accept any loopback port; Auth0 needs one fixed port, so also set `prikboard.oidc.redirectPort`). Let it request the API's `boards:read` and `boards:write` scopes.
2. On the API, trust the issuer and list this application's client id, and use the same id for the issuer and your browser sign-in provider (see *Interactive apps acting for a person* in `docs/authentication.md`):

   ```json
   "Auth": { "Tokens": { "Issuers": { "auth0": {
     "Authority": "https://your-tenant.eu.auth0.com/",
     "UserClients": [ "<client id of the extension>" ]
   } } } }
   ```
3. In VS Code settings, fill in `prikboard.oidc.authority` and `prikboard.oidc.clientId` (and `prikboard.oidc.scopes` / `prikboard.oidc.audience` if your issuer names them differently). **Sign in** then uses the provider. The extension keeps the tokens in VS Code's secret storage and renews them itself.

**With an access token.** **Sign in with an Access Token** takes a personal access token you created in PrikBoard (Account, Agents and access tokens). That token acts as its own agent with the board access you gave it, so PrikBoard does not know it is you.

## Settings

The address and sign-in settings (all but `prikboard.refreshSeconds`) only count in your user settings. VS Code ignores them in a workspace's or folder's `.vscode/settings.json`, so a repository you open can't send your sign-in to another server. Your sign-in also remembers the server it was made for: changing the address signs you out instead of sending it to the new one.

| Setting | Default | Meaning |
| --- | --- | --- |
| `prikboard.serverUrl` | `http://localhost:5080` | Your PrikBoard address, which serves the web app and the API; asked for on the first sign-in |
| `prikboard.webUrl` | empty | Only when the web app runs somewhere else than `serverUrl`, for **Sign in** and **Open in PrikBoard** |
| `prikboard.refreshSeconds` | 15 | How often the open view reloads |
| `prikboard.oidc.authority` | empty | Optional: issuer URL of an identity provider to sign in with instead of PrikBoard |
| `prikboard.oidc.clientId` | empty | Client id of the extension's application at that provider |
| `prikboard.oidc.scopes` | `openid offline_access boards:read boards:write` | Scopes to request, named as your issuer names them |
| `prikboard.oidc.audience` | `api://prikboard` | Sent as `audience` (Auth0 style); empty for issuers that take it from the scopes |
| `prikboard.oidc.redirectPort` | 0 | Fixed local port for the redirect, when the issuer needs the exact URI; 0 picks a free one |

## Code

The extension reuses the web app's API client and types (`src/web/src/api`) instead of keeping a copy. esbuild bundles them in, so those files (`client.ts`, `types.ts` and `cardLinks.ts`) must stay free of DOM and React imports. `configureClient` points the client at a server and adds the bearer token.

```bash
npm run build      # type check and bundle to dist/
npm run package    # build a .vsix
```

## Releasing

The extension is released apart from the app. Push a tag `vscode-1.2.3` and the [VS Code extension workflow](https://github.com/PumpUpTheJams/PrikBoard/blob/main/.github/workflows/extension.yml) packages it as version 1.2.3 and publishes the .vsix as its own GitHub release, *PrikBoard for VS Code 1.2.3*, which never becomes the latest release (that stays the app's). It also pushes the .vsix to the GitHub Container Registry, so it shows under the repository's Packages as `prikboard-vscode`; get it from there with `oras pull ghcr.io/pumpupthejams/prikboard-vscode:1.2.3`. App tags (`1.2.3`) build only the container image.

```bash
git tag vscode-1.2.3 && git push origin vscode-1.2.3
```
