<div align="center">

# PrikBoard

**No bloat, just board.**

A freeform board of cards for small projects, built for people and AI agents alike.

[![CI](https://github.com/PumpUpTheJams/PrikBoard/actions/workflows/ci.yml/badge.svg)](https://github.com/PumpUpTheJams/PrikBoard/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/PumpUpTheJams/PrikBoard?sort=semver)](https://github.com/PumpUpTheJams/PrikBoard/releases)
[![Docker Hub](https://img.shields.io/docker/pulls/prikboard/prikboard)](https://hub.docker.com/r/prikboard/prikboard)
[![License: MIT + Commons Clause](https://img.shields.io/badge/license-MIT%20%2B%20Commons%20Clause-blue)](LICENSE)

[Quick start](#quick-start) · [Features](#features) · [AI agents](#for-ai-agents) · [Developing](#developing) · [Docs](docs/README.md)

</div>

---

Stick cards anywhere on an endless canvas, draw buckets such as *To do*, *Doing* and *Done* around them, and drag cards from one bucket to the next. Everything a person can do in the browser, an AI agent can do too, through the same REST API or PrikBoard's MCP server.

## Features

- **A freeform canvas.** Cards, buckets, images and freehand drawings go wherever you put them. Buckets fold away, stack and grow to fit.
- **Rich cards.** Each card is a small canvas of its own, with Markdown text, checklists, links, images and drawings, plus tags, assignees and a full change history.
- **Live together.** Everyone's changes show up at once, with presence, cursors and selections.
- **Undo and restore.** Undo and redo work on the board and inside cards, and deleted cards, buckets, images and tags can be restored for 7 days.
- **Copy and paste between boards,** even between PrikBoard servers, from a single card to a whole board.
- **Search and filter** by text, tag, assignee, creator or color, and jump to any card by its readable id (`PRIK-42`).
- **Public boards** for anyone with the link, read-only, with or without the names of the people on them.
- **Themes and a phone layout.** Eight built-in themes, from the dark *Productivity* to the playful *Pool party*.
- **Private by design.** PrikBoard never stores names, email addresses or account ids from your sign-in provider, only keyed hashes.

## Quick start

PrikBoard runs as one container next to PostgreSQL, on Linux, macOS or Windows, amd64 or arm64 (a Raspberry Pi works).

**1. Check the prerequisites.** You need Git and Docker with Compose v2. This should print a version:

```bash
docker compose version
```

**2. Get the compose files.**

```bash
git clone https://github.com/PumpUpTheJams/PrikBoard.git
cd PrikBoard/infra/compose
```

**3. Set the database password.** Copy the example settings, then open `.env` and change `POSTGRES_PASSWORD`:

```bash
cp .env.example .env
```

Do this before the first start: PostgreSQL keeps the password it was created with. `.env` also sets the version (`PRIKBOARD_VERSION`, `latest` by default) and the port (`PRIKBOARD_PORT`, `8080`).

**4. Pull the image and start.** This starts PostgreSQL and PrikBoard, which waits for the database and sets it up:

```bash
docker compose pull
docker compose up -d
```

**5. Get the setup key.** PrikBoard prints it in its log until it is set up, a new one on every start:

```bash
docker compose logs app | grep "setup key"
```

**6. Open PrikBoard** at http://localhost:8080, enter the setup key, and follow the setup:

- Add a sign-in provider: GitHub in one click, or Google, Discord, Slack, Steam or any OpenID Connect provider.
- Sign in as the first admin.
- Download the recovery kit.

PrikBoard is invite-only: after the admin, people join through board invites. It listens on localhost only; for HTTPS on your own domain, see [`infra/README.md`](infra/README.md).

**7. Update later.** Back up both volumes first (`postgres-data` for the database, `data` for images and sign-in keys), then get the latest compose files and image. PrikBoard updates the database itself when it starts:

```bash
git pull
docker compose pull
docker compose up -d
```

<details>
<summary><b>Without a checkout:</b> one <code>compose.yaml</code></summary>

The image is on the GitHub Container Registry (`ghcr.io/pumpupthejams/prikboard`) and Docker Hub (`prikboard/prikboard`), as `latest` or a version such as `1.2.3`, for amd64 and arm64. Save this as `compose.yaml`, change the database password in both places, and run `docker compose up -d` next to it. It is a trimmed [`infra/compose/compose.yaml`](infra/compose/compose.yaml), without the Caddy HTTPS profile.

```yaml
services:
  postgres:
    image: postgres:18
    environment:
      POSTGRES_USER: prikboard
      POSTGRES_PASSWORD: change-me
      POSTGRES_DB: prikboard
    volumes:
      - postgres-data:/var/lib/postgresql   # from 18 on, not .../data: the image keeps a folder per major version here
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U prikboard -d prikboard"]
      interval: 5s
      timeout: 5s
      retries: 10
    restart: unless-stopped

  app:
    image: prikboard/prikboard:latest
    environment:
      ConnectionStrings__PrikBoard: Host=postgres;Database=prikboard;Username=prikboard;Password=change-me;GSS Encryption Mode=Disable
    volumes:
      - data:/data   # images, sign-in keys and the hash key: back it up with postgres-data
    ports:
      # Localhost only: the app trusts X-Forwarded-* headers. Reach it from elsewhere through a reverse proxy or tunnel.
      - "127.0.0.1:8080:8080"
    depends_on:
      postgres:
        condition: service_healthy
    restart: unless-stopped

volumes:
  postgres-data:
  data:
```

</details>

## For AI agents

There is one API and nothing is private to the UI. An agent signs in with a personal access token (Account → *Agents and access tokens*), limited to the boards and the role you give it, and appears on the board as an agent of its own.

- **MCP:** PrikBoard is an [MCP](https://modelcontextprotocol.io) server at `/mcp`. For example, in Claude Code:

  ```bash
  claude mcp add --transport http prikboard https://<your-prikboard>/mcp --header "Authorization: Bearer pb_pat_..."
  ```

- **REST:** the OpenAPI document at `/openapi/v1.json` is the contract, with an interactive reference at `/scalar`. See the [API guide](docs/api.md) and the [example requests](src/PrikBoard.Api/PrikBoard.Api.http).
- **Agent rules:** board owners can write rules that agents are told to follow on that board.
- **Machines:** OAuth 2.0 access tokens (Client Credentials) from an identity provider you trust work too, see [Authentication](docs/authentication.md).

## Integrations

- **Slack and Discord:** link previews that follow their cards, board updates in the channels you pick, and *Save as card* on any message. An admin sets them up under Admin → *Instance*. See [Slack](docs/slack.md#setting-it-up) and [Discord](docs/discord.md).
- **VS Code:** your boards and the cards assigned to you in the side bar. See [`src/vscode`](src/vscode/README.md).

## Developing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js 22+](https://nodejs.org) and Docker.

```bash
docker compose up -d                           # PostgreSQL, data kept in the postgres18-data volume
dotnet run --project src/PrikBoard.Api         # API on http://localhost:5080, runs the migrations on startup
cd src/web && npm install && npm run dev       # web app on http://localhost:5173, proxies /api to the API
dotnet test --solution PrikBoard.slnx          # API tests, against the compose database
```

Signing in needs a provider: a GitHub OAuth App (the default) with its client id and secret in user secrets, or one added in the setup. [Authentication](docs/authentication.md) walks through it, including Google, Microsoft, Auth0 and other OpenID Connect providers.

| Folder | Contents |
| --- | --- |
| [`src/PrikBoard.Api`](src/PrikBoard.Api) | ASP.NET Core (.NET 10) minimal API, EF Core and PostgreSQL |
| [`src/PrikBoard.Api.Tests`](src/PrikBoard.Api.Tests) | xUnit integration tests that run the API in memory |
| [`src/web`](src/web) | React 19, TypeScript and Vite web app |
| [`src/vscode`](src/vscode) | The VS Code extension |
| [`infra`](infra) | Self-hosting: compose files and Caddy for HTTPS |
| [`tools`](tools) | A fake Slack for testing, and performance measuring |
| [`docs`](docs/README.md) | Architecture, API, authentication and integration docs |

Start with [Architecture](docs/architecture.md) for how the pieces fit together.

## Contributing and security

Contributions are welcome: see [`CONTRIBUTING.md`](CONTRIBUTING.md). Please report security problems privately, as described in [`SECURITY.md`](SECURITY.md), not in a public issue.

## License

PrikBoard is licensed under the [MIT license with the Commons Clause](LICENSE). That makes it source-available rather than open source in the OSI sense:

- Anyone, companies included, may use, change, self-host and build on PrikBoard, also for commercial work.
- Nobody may sell PrikBoard itself, or a product or service (such as paid PrikBoard hosting or support) whose value comes entirely or substantially from it.
