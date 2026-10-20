# PrikBoard API. Build from this folder: docker build -t prikboard-api src/PrikBoard.Api
# Multi-platform (linux/amd64, linux/arm64): the SDK stage always runs on the build machine's own platform and
# cross-compiles for TARGETARCH, so an arm64 image builds at full speed on an x64 runner, without emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY PrikBoard.Api.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY . ./
RUN dotnet publish --no-restore -a $TARGETARCH -c Release -o /app
# Created up front so a volume mounted here (to keep sign-in sessions across restarts) is owned by "app". Made in
# this stage and copied with --chown, because a RUN in the final stage would need emulation for other platforms.
RUN mkdir -p /home-app/.aspnet/DataProtection-Keys

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=app:app /home-app/.aspnet /home/app/.aspnet
# The image runs as the non-root "app" user and listens on 8080. It is meant to sit behind the web container (or
# another reverse proxy), so trust X-Forwarded-Proto/-For: OAuth callback URLs must use the scheme the browser sees.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "PrikBoard.Api.dll"]
