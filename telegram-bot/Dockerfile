# syntax=docker/dockerfile:1

# Long-running background worker (no HTTP port, no health check needed - it
# polls Telegram over outbound HTTPS). Run it as a docker-compose service, a
# systemd service, or a PaaS "background worker" - see DEPLOYMENT.md.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY telegram-bot/src/FHSMS.TelegramBot.csproj telegram-bot/src/
RUN dotnet restore telegram-bot/src/FHSMS.TelegramBot.csproj

COPY telegram-bot/src/ telegram-bot/src/
RUN dotnet publish telegram-bot/src/FHSMS.TelegramBot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
USER app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "FHSMS.TelegramBot.dll"]
