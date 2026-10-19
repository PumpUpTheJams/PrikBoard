# PrikBoard for VS Code

Your PrikBoard boards, buckets and notes in the side bar. Work in progress: the view is read-only for now.

## Use it

1. Start the API (`dotnet run --project src/PrikBoard.Api`).
2. Open this folder (`src/vscode`) in VS Code, run `npm install`, and press F5.
3. In the PrikBoard view, choose **Sign in with an access token** and paste a personal access token (PrikBoard: Account, Agents and access tokens). The token is kept in VS Code's secret storage.

A personal access token acts as its own agent with the board access you gave it, so boards must be visible to that token.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `prikboard.serverUrl` | `http://localhost:5080` | Where the API runs |
| `prikboard.webUrl` | `http://localhost:5173` | Where the web app runs, for **Open in PrikBoard** |
| `prikboard.refreshSeconds` | 15 | How often the open view reloads |

## Code

The extension reuses the web app's API client and types (`src/web/src/api`) instead of keeping a copy. esbuild bundles them in, so those two files must stay free of DOM and React imports. `configureClient` points the client at a server and adds the bearer token.

```bash
npm run build      # type check and bundle to dist/
npm run package    # build a .vsix
```
