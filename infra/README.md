# Infrastructure

Local development needs no infrastructure: the API stores its data in PostgreSQL from the root `compose.yaml` and the Vite dev server proxies API calls.

## Running PrikBoard on a server

[`compose/compose.yaml`](compose/compose.yaml) runs PrikBoard with PostgreSQL on one machine:

```bash
cd infra/compose
cp .env.example .env    # optional: version, port, database password, domain
docker compose up -d
docker compose logs app # shows the setup key
```

Open http://localhost:8080 and follow the setup: enter the setup key, add sign-in providers, sign in as the first admin, run the checks and download the recovery kit ([Authentication](../docs/authentication.md#setup-and-recovery)).

- **One image.** The root [`Dockerfile`](../Dockerfile) builds the web app and the API into one image, in which the API serves the web app from `wwwroot` (port 8080, non-root). The [Containers workflow](../.github/workflows/containers.yml) publishes it to `ghcr.io/<owner>/prikboard` with the version and `latest` when a semver tag such as `1.2.3` is pushed, for `linux/amd64` and `linux/arm64` (it cross-compiles on the build machine's own platform, so no emulation is needed). Without a published image, `docker compose up` builds it from this checkout.
- **One data folder.** `/data` (`Storage__Path`) holds the images and avatars, the Data Protection keys (`keys/`: sessions and stored provider secrets) and the hash key generated on the first start (`hash-key`). Both are stored unencrypted on purpose, so the folder's permissions are what protects them. Keep it on a volume (the compose file does) and back it up together with the database volume. The first start of a version from before files moved out of the database moves them there.
- **Localhost only.** The app trusts `X-Forwarded-Proto/-For` (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`), so the compose file publishes it on `127.0.0.1` only. To reach it from elsewhere, use the `https` profile or a tunnel.
- **HTTPS.** Point a domain's DNS at the machine, open ports 80 and 443, set `DOMAIN` in `.env` and start `docker compose --profile https up -d`: [Caddy](compose/Caddyfile) gets a certificate and proxies to the app. A tunnel (Cloudflare Tunnel, Tailscale Funnel) pointed at `app:8080` works too. Sign-in providers need HTTPS on any address but localhost, and their callback URLs name the address, so add the providers again (a new GitHub App) for the new address.
- **One instance.** Live presence and the sign-in provider schemes live in the app's memory, so run one replica.
- The image is based on the chiseled .NET runtime, which has no shell, so `docker exec` into it doesn't work; use `docker compose logs app`, or `docker run --rm -v prikboard-server_data:/data alpine` to look at the data volume.

## Configuration

Everything has a default; these override it, as environment variables on the app:

- `ConnectionStrings__PrikBoard`: the database (set by the compose file).
- `Auth__HashKey`: your own hash key instead of the generated one.
- `Auth__Providers__{id}__...`: sign-in providers in configuration instead of the setup (read-only there), and `Auth__Tokens__Issuers__{id}__Authority` for trusted token issuers.
- `Setup__Recovery=true`: print a setup key although users exist, to fix the sign-in providers.
- `DataProtection__KeysPath`: keep the Data Protection keys outside the data folder.
- `OpenTelemetry__Endpoint`: an OTLP collector's base URL (e.g. `http://otel-collector:4317`); when set, the app sends its logs, traces (requests, outgoing HTTP, database) and metrics there. `OpenTelemetry__Protocol` is `grpc` (default) or `http/protobuf` (port 4318), and `OpenTelemetry__Headers` takes `key=value,key2=value2`, for an API key. Off by default.
- `Serilog__MinimumLevel__Default`, `Serilog__MinimumLevel__Override__{category}`: more or less logging (see below).

## Logs

The app logs to the console (`docker compose logs app`) through [Serilog](https://serilog.net), and only what matters: its own messages (the setup key, generated hash key, moved and purged files, sign-in provider problems), warnings and errors from the framework, startup and migrations, and one line per request that fails with a 5xx or takes longer than a second. Database queries and other framework chatter are left out. The levels are in the `Serilog` section of [`appsettings.json`](../src/PrikBoard.Api/appsettings.json); to see the SQL, for example, set `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Information`. The Discord bot works the same way.

## Open decisions

- Infrastructure as code for a cloud platform, for example Azure Container Apps with a managed PostgreSQL.
