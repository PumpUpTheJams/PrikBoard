# PrikBoard in one image: the API, which also serves the built web app. Build from the repository root:
#   docker build -t prikboard .
# Run it with infra/compose/compose.yaml (PostgreSQL next to it, a volume on /data), then open it and follow the setup.
# Multi-platform (linux/amd64, linux/arm64): the Node and SDK stages always run on the build machine's own platform (the
# SDK cross-compiles for TARGETARCH), so an arm64 image builds at full speed on an x64 runner, without emulation.
FROM --platform=$BUILDPLATFORM node:22-slim AS web
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api
ARG TARGETARCH
WORKDIR /src
COPY src/PrikBoard.Api/PrikBoard.Api.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY src/PrikBoard.Api/ ./
# The release version (the git tag), reported by GET /api/auth/version. Without it the assembly says 1.0.0.
ARG VERSION=1.0.0
RUN dotnet publish --no-restore -a $TARGETARCH -c Release -o /app -p:Version=$VERSION
# Created up front so the volume mounted here is owned by "app". Made in this stage and copied with --chown, because a
# RUN in the final stage would need emulation for other platforms.
RUN mkdir -p /data

# Chiseled: only the .NET runtime and its dependencies, no shell or package manager, about 70 MB smaller than aspnet:10.0.
# The -extra variant keeps ICU and time zone data, which Npgsql and culture-aware string handling need.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
# The data folder: images and avatars, the Data Protection keys (sign-in sessions) and the generated hash key. Back it
# up together with the database.
COPY --from=api --chown=app:app /data /data
VOLUME /data
# The image runs as the non-root "app" user and listens on 8080. It is meant to sit behind a reverse proxy (Caddy in the
# compose file) or to be published on localhost only, so it trusts X-Forwarded-Proto/-For: OAuth callback URLs must
# use the scheme the browser sees.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Storage__Path=/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "PrikBoard.Api.dll"]
