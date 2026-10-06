#!/usr/bin/env bash
# Guided first-time setup for a Linux server (Ubuntu/Debian recommended) using Docker.
#   bash deploy/setup.sh
# Creates .env with strong random secrets, then builds and starts everything.
# Safe to re-run: it will not overwrite an existing .env.
set -euo pipefail

cd "$(dirname "$0")/.."
COMPOSE_FILE="docker-compose.prod.yml"

say()  { printf '\n\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

command -v openssl >/dev/null 2>&1 || die "openssl is required (sudo apt install -y openssl)."

# ---- 1. Docker ------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  warn "Docker is not installed."
  read -r -p "Install Docker now using the official script? [Y/n] " ans
  if [[ "${ans:-Y}" =~ ^[Yy]$ ]]; then
    curl -fsSL https://get.docker.com | sh
  else
    die "Install Docker (https://docs.docker.com/engine/install/) and re-run."
  fi
fi
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 plugin not found (sudo apt install -y docker-compose-plugin)."
DOCKER="docker"
if ! docker info >/dev/null 2>&1; then DOCKER="sudo docker"; fi

# ---- 2. .env ------------------------------------------------------------------
if [[ -f .env ]]; then
  say ".env already exists - keeping it. (Delete it and re-run to start over.)"
else
  say "Let's create your settings."
  echo "Enter your domain name (e.g. agrilink.example.com) for free automatic HTTPS,"
  echo "or your server's public IP address to run on plain HTTP for now."
  read -r -p "Domain or IP: " HOST
  [[ -n "$HOST" ]] || die "A domain or IP is required."
  HOST="${HOST#http://}"; HOST="${HOST#https://}"; HOST="${HOST%%/*}"

  if [[ "$HOST" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ || "$HOST" == "localhost" ]]; then
    SITE_ADDRESS=":80"; PUBLIC_URL="http://$HOST"
  else
    SITE_ADDRESS="$HOST"; PUBLIC_URL="https://$HOST"
    warn "Make sure DNS for $HOST points to this server and ports 80 and 443 are open,"
    warn "otherwise the HTTPS certificate cannot be issued."
  fi

  read -r -p "Admin email [admin@fhsms.local]: " ADMIN_EMAIL
  ADMIN_EMAIL="${ADMIN_EMAIL:-admin@fhsms.local}"
  read -r -p "Admin password (leave empty to generate one): " ADMIN_PASSWORD
  GENERATED_ADMIN=""
  if [[ -z "${ADMIN_PASSWORD:-}" ]]; then
    ADMIN_PASSWORD="Fh$(openssl rand -hex 6)Aa9"; GENERATED_ADMIN="yes"
  fi
  [[ "$ADMIN_PASSWORD" =~ ^[A-Za-z0-9_.@-]{8,}$ ]] || die "Use 8+ characters: letters, numbers and _ . @ - only."

  read -r -p "Telegram bot token (optional, press Enter to skip): " BOT_TOKEN

  {
    echo "SITE_ADDRESS=$SITE_ADDRESS"
    echo "PUBLIC_URL=$PUBLIC_URL"
    echo "POSTGRES_DB=fhsms"
    echo "POSTGRES_USER=fhsms_user"
    echo "POSTGRES_PASSWORD=$(openssl rand -hex 20)"
    echo "JWT_SECRET=$(openssl rand -hex 32)"
    echo "ADMIN_EMAIL=$ADMIN_EMAIL"
    echo "ADMIN_PASSWORD=$ADMIN_PASSWORD"
    if [[ -n "${BOT_TOKEN:-}" ]]; then
      echo "TELEGRAM_BOT_TOKEN=$BOT_TOKEN"
      echo "COMPOSE_PROFILES=telegram"
    fi
  } > .env
  chmod 600 .env
  say "Created .env (only you can read it)."
fi

# ---- 3. Build + start -----------------------------------------------------------
say "Building and starting FHSMS (the first build takes a few minutes)..."
$DOCKER compose -f "$COMPOSE_FILE" up -d --build

say "Waiting for the API to become healthy..."
for i in $(seq 1 60); do
  status="$($DOCKER compose -f "$COMPOSE_FILE" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1=="api"{print $2}')"
  [[ "$status" == "healthy" ]] && break
  sleep 3
done

PUBLIC_URL_VALUE="$(grep -E '^PUBLIC_URL=' .env | cut -d= -f2-)"
say "Done."
echo "  Website : $PUBLIC_URL_VALUE"
echo "  Health  : $PUBLIC_URL_VALUE/health"
if [[ -n "${GENERATED_ADMIN:-}" ]]; then
  echo "  Admin   : $ADMIN_EMAIL  /  $ADMIN_PASSWORD   <-- write this down, change it after login"
else
  echo "  Admin   : $(grep -E '^ADMIN_EMAIL=' .env | cut -d= -f2-)  (password as set in .env)"
fi
echo
echo "Logs:    $DOCKER compose -f $COMPOSE_FILE logs -f"
echo "Update:  git pull && $DOCKER compose -f $COMPOSE_FILE up -d --build"
echo "Backup:  bash deploy/backup.sh"
