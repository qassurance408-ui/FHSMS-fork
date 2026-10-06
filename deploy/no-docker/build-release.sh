#!/usr/bin/env bash
# Builds everything into ./release so you can upload it to a server WITHOUT Docker
# (via scp / rsync / FTP / the host's file manager).
#
#   bash deploy/no-docker/build-release.sh
#
# Needs on THIS machine: .NET 8 SDK and Node 20+.
# Output:
#   release/api      ASP.NET Core API   (self-contained: the server needs no .NET installed)
#   release/bot      Telegram bot       (same)
#   release/web      static website     (upload to your web root)
#
# Target platform (default linux-x64). For a Windows server use RID=win-x64,
# for ARM Linux use RID=linux-arm64. For a smaller framework-dependent build
# (server must have the .NET 8 runtime installed) use SELF_CONTAINED=false.
set -euo pipefail
cd "$(dirname "$0")/../.."

RID="${RID:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-true}"
OUT="release"

rm -rf "$OUT"; mkdir -p "$OUT"

echo "==> Publishing API ($RID, self-contained=$SELF_CONTAINED)"
dotnet publish src/FHSMS.API/FHSMS.API.csproj -c Release -r "$RID" --self-contained "$SELF_CONTAINED" -o "$OUT/api"

echo "==> Publishing Telegram bot"
dotnet publish telegram-bot/src/FHSMS.TelegramBot.csproj -c Release -r "$RID" --self-contained "$SELF_CONTAINED" -o "$OUT/bot"

echo "==> Building website"
( cd frontend && npm install && npm run build )
cp -r frontend/dist "$OUT/web"

echo
echo "Done. Upload the three folders in ./$OUT to your server - see DEPLOYMENT.md, section 'Option B'."
