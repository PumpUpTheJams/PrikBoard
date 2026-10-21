# PrikBoard Discord bot. Build from this folder: docker build -t prikboard-discord src/PrikBoard.Discord
# Like the API image, the SDK stage runs on the build machine's own platform and cross-compiles for TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY PrikBoard.Discord.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY . ./
RUN dotnet publish --no-restore -a $TARGETARCH -c Release -o /app
# Created up front so a volume mounted here (the servers' tokens and their encryption keys) is owned by "app".
RUN mkdir -p /home-app/data

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=app:app /home-app/data /home/app/data
# The bot connects out to Discord's gateway and to the API; it listens on nothing.
# Set Discord__BotToken, PrikBoard__ApiUrl and PrikBoard__PublicUrl, and keep Storage__Path on a volume.
ENV Storage__Path=/home/app/data
USER app
ENTRYPOINT ["dotnet", "PrikBoard.Discord.dll"]
