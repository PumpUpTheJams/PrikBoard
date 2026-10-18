# PrikBoard

A post-it board for small project management. Stick notes anywhere on a board, draw buckets such as *To do* and *Done* around them, and drag notes from one bucket to the next.
The web UI and AI agents use the same REST API, which is described by an OpenAPI document.

## Repository layout

| Folder | Contents |
| --- | --- |
| [`src/PrikBoard.Api`](src/PrikBoard.Api) | ASP.NET Core (.NET 10) minimal API, EF Core + SQLite |
| [`src/PrikBoard.Api.Tests`](src/PrikBoard.Api.Tests) | xUnit integration tests running the API in memory |
| [`src/web`](src/web) | React 19 + TypeScript + Vite frontend |
| [`infra`](infra) | Hosting and deployment (not set up yet) |
| [`docs`](docs) | Architecture and API documentation |

## Getting started

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js 22+](https://nodejs.org).

Start the API on http://localhost:5080. It creates and migrates `prikboard.db` on first run:

```bash
dotnet run --project src/PrikBoard.Api
```

In a second terminal, start the frontend on http://localhost:5173. Vite proxies `/api` to the API:

```bash
cd src/web && npm install && npm run dev
```

Run the tests:

```bash
dotnet test PrikBoard.slnx
```

## Using the API (humans and AI agents)

- OpenAPI document: http://localhost:5080/openapi/v1.json
- Example requests: [`src/PrikBoard.Api/PrikBoard.Api.http`](src/PrikBoard.Api/PrikBoard.Api.http)
- Guide: [`docs/api.md`](docs/api.md)

See [`docs/architecture.md`](docs/architecture.md) for how the pieces fit together.
