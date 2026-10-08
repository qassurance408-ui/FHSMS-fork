# syntax=docker/dockerfile:1

# Web container: serves the built React app AND reverse-proxies /api to the
# API container, with automatic HTTPS when you give it a domain name. One
# container, works on any Docker host (Alet Cloud VPS, any VPS, a home
# server, ...). Build context is the repo root - see docker-compose.prod.yml.

FROM node:20-alpine AS build
WORKDIR /app

# No package-lock.json is committed yet, so `npm install` is used. Commit a
# lockfile and switch to `npm ci` for reproducible builds.
COPY frontend/package.json ./
RUN npm install

COPY frontend/ ./

# Leave empty for the normal same-domain setup (the app then calls "/api").
# Only set this if the API lives on a different domain:
#   docker build --build-arg VITE_API_BASE_URL=https://api.example.com/api ...
# You can also change it later, without rebuilding, by editing config.js.
ARG VITE_API_BASE_URL=""
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

FROM caddy:2-alpine AS runtime
COPY --from=build /app/dist /srv
COPY frontend/Caddyfile /etc/caddy/Caddyfile

# SITE_ADDRESS  ":80" (default) = plain HTTP on any hostname/IP.
#               "app.example.com" = automatic HTTPS (needs DNS pointing here
#               and ports 80 + 443 open).
# API_UPSTREAM  where /api is forwarded - the compose service name by default.
ENV SITE_ADDRESS=":80" \
    API_UPSTREAM="api:8080"

EXPOSE 80 443
