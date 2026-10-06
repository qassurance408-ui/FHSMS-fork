# Deploying AgriLink / FHSMS on any host

This system runs on **any** hosting: Alet Cloud, any other Ethiopian or
international VPS, a Windows/IIS server, an office server, or a PaaS such as
Render. Nothing in the code is tied to one provider - everything is configured
with environment variables.

## Which option should I use?

| Your hosting | Use | Effort |
|---|---|---|
| **A Linux VPS / cloud server** (Alet Cloud VM, DigitalOcean, Hetzner, ...) | **Option A - Docker** (recommended) | ~10 min, one command |
| A Linux VPS where you prefer no Docker | **Option B - systemd + nginx** | ~30 min |
| A **Windows** server / Windows hosting with IIS | **Option D - IIS** | ~30 min |
| **cPanel / shared hosting** (PHP-style hosting) | **Option C** - website there, API somewhere else | ~20 min |
| Render / Railway / other PaaS | see [`docs/DEPLOYMENT-RENDER.md`](docs/DEPLOYMENT-RENDER.md) | - |

**What runs:** a PostgreSQL database, the .NET 8 API, the website (static files),
and optionally the Telegram bot. The website and the API are served from the
**same domain** (`/api` goes to the backend), so there is no CORS to configure.

> Alet Cloud: create a VM/VPS running **Ubuntu 22.04 or 24.04** (2 GB RAM or
> more - 1 GB can run out of memory while building), open ports **22, 80, 443**
> in its firewall/security group, and follow Option A. If Alet offers a managed
> PostgreSQL you can use it instead of the bundled database - see
> "Using a managed database" below.

---

## Option A - Docker on a Linux server (recommended)

1. **Get the code onto the server** (`git clone <your repo>` or upload the zip and `unzip` it), then `cd` into the folder.
2. **Point a domain at the server** (optional but recommended): create a DNS `A` record such as `agrilink.example.com -> <server IP>`. No domain yet? You can use the server IP and plain HTTP, and add a domain later.
3. **Run the guided setup:**
   ```bash
   bash deploy/setup.sh
   ```
   It installs Docker if needed, asks for your domain/IP, admin login and (optionally) Telegram token, creates `.env` with strong random secrets, and builds and starts everything.
4. Open the address it prints. Log in with the admin account shown, then **change the password**.

Prefer to do it by hand?
```bash
cp .env.example .env        # edit: SITE_ADDRESS, PUBLIC_URL, passwords, JWT_SECRET
docker compose -f docker-compose.prod.yml up -d --build
```

**Everyday commands**
```bash
docker compose -f docker-compose.prod.yml ps               # status
docker compose -f docker-compose.prod.yml logs -f api      # API logs (also: web, postgres, bot)
git pull && docker compose -f docker-compose.prod.yml up -d --build   # update to a new version
bash deploy/backup.sh                                      # database backup -> backups/
bash deploy/restore.sh backups/<file>.sql.gz               # restore (erases current data)
```
Automatic daily backup: `crontab -e` and add
`0 2 * * * cd /opt/fhsms && bash deploy/backup.sh >> backups/backup.log 2>&1`
(adjust the path). **Also copy backups off the server.**

**HTTPS:** with `SITE_ADDRESS=your.domain.com` the `web` container obtains and renews a free Let's Encrypt certificate automatically. It needs DNS pointing to the server and ports 80 and 443 reachable. With an IP address (`SITE_ADDRESS=:80`) you get plain HTTP.

**Telegram bot:** put `TELEGRAM_BOT_TOKEN=...` and `COMPOSE_PROFILES=telegram` in `.env`, then `docker compose -f docker-compose.prod.yml up -d`.

---

## Option B - Linux without Docker (systemd + nginx)

Prepare on any machine with the **.NET 8 SDK** and **Node 20+** (your laptop is fine):
```bash
bash deploy/no-docker/build-release.sh      # creates ./release/{api,bot,web}
```
The API and bot are built *self-contained*, so the server does **not** need .NET installed. Then on the server (Ubuntu/Debian):

```bash
sudo apt install -y nginx postgresql
sudo -u postgres psql -c "CREATE USER fhsms_user WITH PASSWORD 'CHOOSE_A_PASSWORD';"
sudo -u postgres psql -c "CREATE DATABASE fhsms OWNER fhsms_user;"

sudo useradd --system --home /opt/fhsms --shell /usr/sbin/nologin fhsms
sudo mkdir -p /opt/fhsms /etc/fhsms /var/www/fhsms
# upload release/api -> /opt/fhsms/api, release/bot -> /opt/fhsms/bot, release/web/* -> /var/www/fhsms
sudo chown -R fhsms:fhsms /opt/fhsms && sudo chmod +x /opt/fhsms/api/FHSMS.API /opt/fhsms/bot/FHSMS.TelegramBot

sudo cp deploy/no-docker/api.env.example /etc/fhsms/api.env     # edit it (DB password, JWT secret, domain, admin)
sudo chmod 600 /etc/fhsms/api.env
sudo cp deploy/no-docker/fhsms-api.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now fhsms-api

sudo cp deploy/no-docker/nginx-fhsms.conf /etc/nginx/sites-available/fhsms   # edit server_name
sudo ln -s /etc/nginx/sites-available/fhsms /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo apt install -y certbot python3-certbot-nginx && sudo certbot --nginx -d your.domain.com   # HTTPS
```
Telegram bot (optional): copy `bot.env.example` to `/etc/fhsms/bot.env`, `fhsms-bot.service` to `/etc/systemd/system/`, then `sudo systemctl enable --now fhsms-bot`.
Logs: `journalctl -u fhsms-api -f`.

---

## Option C - cPanel / shared hosting (website only)

Shared hosting normally cannot run the .NET API, so split it:

* **API + database:** run on a VPS (Option A or B) - e.g. `https://api.example.com`.
* **Website:** on the shared host. Build it (`cd frontend && npm install && npm run build`) or take `release/web`, and upload **everything inside `dist/`** (including the hidden `.htaccess`) to `public_html`.
* Edit **`config.js`** in `public_html` (no rebuild needed):
  ```js
  window.__FHSMS_CONFIG__ = { apiBaseUrl: "https://api.example.com/api" };
  ```
* On the API server set `ALLOWED_ORIGINS=https://your-website-domain.com` so the browser is allowed to call it.

## Option D - Windows / IIS

1. Install the **.NET 8 Hosting Bundle** and the **URL Rewrite** module on the server.
2. On a build machine: `dotnet publish src/FHSMS.API -c Release -o publish` and copy `publish` to e.g. `C:\inetpub\fhsms-api` (IIS site or application; app pool "No Managed Code").
3. Set environment variables (`ConnectionStrings__DefaultConnection`, `Jwt__Secret`, `ALLOWED_ORIGINS`, ...) in the IIS site's configuration or a `web.config` `<environmentVariables>` block. You need PostgreSQL 14+ (installed locally or a managed database).
4. Website: build `frontend`, copy `dist` (it already contains a working `web.config`) to another IIS site, and set `apiBaseUrl` in `config.js` to the API's address (or use one site with `/api` as a reverse-proxy rule via ARR).

---

## Using a managed database (any provider)

Create an empty PostgreSQL database with your provider, then give the API its address:

* **Docker:** in `.env` set `DATABASE_URL=postgres://user:password@host:5432/dbname` (add `?sslmode=disable` if it does not use SSL) and start only `web` and `api`: `docker compose -f docker-compose.prod.yml up -d --build --no-deps web api`.
* **Without Docker:** set `DATABASE_URL` (or `ConnectionStrings__DefaultConnection`) in `/etc/fhsms/api.env`.

Special characters in a password inside a `postgres://` URL must be URL-encoded (`@` -> `%40`).

## Database schema - read this once

On start-up the API prepares the database itself:

* If the project contains EF Core **migrations** (`src/FHSMS.Infrastructure/Migrations/`), it applies them.
* If it does **not** (the case in this download), it creates all tables directly from the model. That makes the first deploy work with no extra steps.

The catch: a database created that way cannot be *upgraded* automatically when a later version changes the tables. **Before real data goes in**, decide which you want. To switch to proper migrations (recommended for long-term production), generate them once - no .NET install needed, only Docker:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 sh -c \
  'dotnet tool install --global dotnet-ef --version "8.*" && /root/.dotnet/tools/dotnet-ef migrations add InitialCreate --project src/FHSMS.Infrastructure --startup-project src/FHSMS.API'
sudo chown -R "$USER" src     # files were created by root inside the container
```
Commit the new `Migrations/` folder **before the first production start**. If you already started once without it, delete the test database (`docker compose -f docker-compose.prod.yml down -v` wipes it) and start again.

## Configuration reference

| Variable | Purpose |
|---|---|
| `SITE_ADDRESS`, `PUBLIC_URL` | Your domain/IP (Docker setup) - see `.env.example` |
| `POSTGRES_*` / `DATABASE_URL` / `ConnectionStrings__DefaultConnection` | Database |
| `JWT_SECRET` / `Jwt__Secret` | 32+ random characters - keep secret, never reuse |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` / `SEED_ADMIN_*` | First admin login (used once, on an empty database) |
| `ALLOWED_ORIGINS` | Website address(es) allowed to call the API (comma-separated) |
| `PaymentProviders__*` | Telebirr / Chapa / CBE Birr / bank credentials, when you have them |
| `PORT` / `ASPNETCORE_URLS` | Only if your host forces a port |
| `apiBaseUrl` in `config.js` | Point the website at an API on another domain, no rebuild |

## Troubleshooting

* **Blank page or "Network Error" in the browser** - the site cannot reach the API. Open `https://your-site/health`: it should say `{"status":"healthy"}`. If it doesn't: `docker compose ... logs api`.
* **CORS error** - the API is on a different domain than the site: set `ALLOWED_ORIGINS` (API) and `apiBaseUrl` (`config.js`).
* **HTTPS certificate not issued** - DNS must point to this server and ports 80/443 must be open in *both* the server firewall (`sudo ufw allow 80,443/tcp`) and the cloud provider's firewall. Check `docker compose ... logs web`.
* **API restarts in a loop** - usually a database problem; `logs api` shows it. Check the password/`DATABASE_URL`.
* **Photos missing** - the landing-page photos are loaded from Unsplash, so visitors need internet access to that site.
* **Login works but changes to `.env` don't apply** - run `docker compose -f docker-compose.prod.yml up -d` again after editing `.env`. The admin variables only apply on the very first start.
* **Bot silent** - `docker compose ... logs bot`; the admin email/password in `.env` must be a valid login.

## Security checklist before going live

- [ ] Changed the admin password after first login
- [ ] `JWT_SECRET` and database password are long and random (the setup script does this)
- [ ] Only ports 22, 80, 443 open to the internet (the database is never exposed by this setup)
- [ ] Domain with HTTPS
- [ ] Backups scheduled **and** copied off the server
- [ ] `.env` never committed to git or shared
