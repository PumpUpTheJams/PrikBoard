# PrikBoard

A board of cards for small project management. Stick cards anywhere on a board, draw buckets such as *To do* and *Done* around them, and drag cards from one bucket to the next.
The web UI and AI agents use the same REST API, which is described by an OpenAPI document.

## Repository layout

| Folder | Contents |
| --- | --- |
| [`src/PrikBoard.Api`](src/PrikBoard.Api) | ASP.NET Core (.NET 10) minimal API, EF Core + PostgreSQL |
| [`src/PrikBoard.Api.Tests`](src/PrikBoard.Api.Tests) | xUnit integration tests running the API in memory |
| [`src/web`](src/web) | React 19 + TypeScript + Vite frontend |
| [`infra`](infra) | Hosting and deployment (not set up yet) |
| [`docs`](docs) | Architecture and API documentation |

## Getting started

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

Signing in needs a GitHub OAuth App (the default provider) with its client id and secret in user secrets. [`docs/authentication.md`](docs/authentication.md) walks through it, and covers Google, Microsoft, Auth0 and other OpenID Connect providers.

Run the tests. They need the compose database running, and each test class creates and drops its own database on it:

```bash
dotnet test PrikBoard.slnx
```

## Using the API (humans and AI agents)

- Machines authenticate with an OAuth 2.0 access token (Client Credentials) from a trusted identity provider, sent as `Authorization: Bearer <token>`
- Interactive API reference (Scalar): http://localhost:5173/scalar. Signs you in with GitHub first, so "Test Request" uses your session
- OpenAPI document: http://localhost:5080/openapi/v1.json
- Example requests: [`src/PrikBoard.Api/PrikBoard.Api.http`](src/PrikBoard.Api/PrikBoard.Api.http)
- Guide: [`docs/api.md`](docs/api.md)

See [`docs/architecture.md`](docs/architecture.md) for how the pieces fit together.
