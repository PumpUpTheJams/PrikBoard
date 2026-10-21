# PrikBoard API. Build from this folder: docker build -t prikboard-api src/PrikBoard.Api
# Multi-platform (linux/amd64, linux/arm64): the SDK stage always runs on the build machine's own platform and
# cross-compiles for TARGETARCH, so an arm64 image builds at full speed on an x64 runner, without emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY PrikBoard.Api.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY . ./
# The release version (the git tag), reported by GET /api/auth/version. Without it the assembly says 1.0.0.
ARG VERSION=1.0.0
RUN dotnet publish --no-restore -a $TARGETARCH -c Release -o /app -p:Version=$VERSION
# Created up front so volumes mounted here (to keep sign-in sessions across restarts, and for images and avatars) are
# owned by "app". Made in this stage and copied with --chown, because a RUN in the final stage would need emulation for
# other platforms.
RUN mkdir -p /home-app/.aspnet/DataProtection-Keys /home-app/files

# Chiseled: only the .NET runtime and its dependencies, no shell or package manager, about 70 MB smaller than aspnet:10.0.
# The -extra variant keeps ICU and time zone data, which Npgsql and culture-aware string handling need.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=app:app /home-app/.aspnet /home/app/.aspnet
# Images and avatars go to Storage__Path, which is deliberately not set here: without a volume the files would be lost
# on restart, so the API refuses to start until it is given one, e.g. -e Storage__Path=/home/app/files -v files:/home/app/files.
COPY --from=build --chown=app:app /home-app/files /home/app/files
# The image runs as the non-root "app" user and listens on 8080. It is meant to sit behind the web container (or
# another reverse proxy), so trust X-Forwarded-Proto/-For: OAuth callback URLs must use the scheme the browser sees.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "PrikBoard.Api.dll"]
