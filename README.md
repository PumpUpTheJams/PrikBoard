# PrikBoard

A board of cards for small project management. Stick cards anywhere on a board, draw buckets such as *To do* and *Done* around them, and drag cards from one bucket to the next.
The web UI and AI agents use the same REST API, which is described by an OpenAPI document.

## Repository layout

| Folder | Contents |
| --- | --- |
| [`src/PrikBoard.Api`](src/PrikBoard.Api) | ASP.NET Core (.NET 10) minimal API, EF Core + PostgreSQL |
| [`src/PrikBoard.Api.Tests`](src/PrikBoard.Api.Tests) | xUnit integration tests running the API in memory |
| [`src/web`](src/web) | React 19 + TypeScript + Vite frontend |
| [`infra`](infra) | Hosting: the compose file for running PrikBoard on a server |
| [`docs`](docs) | Architecture and API documentation |

## Running PrikBoard

PrikBoard runs as one container next to PostgreSQL. With Docker on the machine:

```bash
cd infra/compose && docker compose up -d
```

Open http://localhost:8080 and follow the setup. It asks for the setup key that PrikBoard prints in its log (`docker compose logs app`), then lets you add sign-in providers (GitHub in one click, Google, Discord, Slack, Steam or any OpenID Connect provider), sign in as the admin and download the recovery kit. For HTTPS on your own domain, see [`infra/README.md`](infra/README.md).

To preview PrikBoard links in chat, post board updates and save messages as cards, an admin can add a Discord bot or a Slack app under Admin → *Instance*: see [Discord](docs/discord.md) and [Slack](docs/slack.md#setting-it-up).

## Developing

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js 22+](https://nodejs.org) and Docker.

Start PostgreSQL from [`compose.yaml`](compose.yaml). Its data is kept in the `postgres-data` volume, so it survives restarts and `docker compose down` (`docker compose down -v` wipes it):

```bash
docker compose up -d
```

Start the API on http://localhost:5080. It applies the database migrations on startup:

```bash
dotnet run --project src/PrikBoard.Api
```

In a second terminal, start the frontend on http://localhost:5173. Vite proxies `/api` to the API:

```bash
cd src/web && npm install && npm run dev
```

Signing in needs a GitHub OAuth App (the default provider) with its client id and secret in user secrets, or a provider added in the setup. [`docs/authentication.md`](docs/authentication.md) walks through it, and covers Google, Microsoft, Auth0 and other OpenID Connect providers.

Run the tests. They need the compose database running, on which they keep a pool of test databases:

```bash
dotnet test --solution PrikBoard.slnx
```

## Using the API (humans and AI agents)

- Machines authenticate with an OAuth 2.0 access token (Client Credentials) from a trusted identity provider, sent as `Authorization: Bearer <token>`
- Interactive API reference (Scalar): http://localhost:5173/scalar. Signs you in with GitHub first, so "Test Request" uses your session
- OpenAPI document: http://localhost:5080/openapi/v1.json
- Example requests: [`src/PrikBoard.Api/PrikBoard.Api.http`](src/PrikBoard.Api/PrikBoard.Api.http)
- Guide: [`docs/api.md`](docs/api.md)

See [`docs/architecture.md`](docs/architecture.md) for how the pieces fit together.
