# PrikBoard API. Build from this folder: docker build -t prikboard-api src/PrikBoard.Api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PrikBoard.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The image runs as the non-root "app" user and listens on 8080. It is meant to sit behind the web container (or
# another reverse proxy), so trust X-Forwarded-Proto/-For: OAuth callback URLs must use the scheme the browser sees.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
# Created up front so a volume mounted here (to keep sign-in sessions across restarts) is owned by "app".
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R app:app /home/app/.aspnet
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "PrikBoard.Api.dll"]
