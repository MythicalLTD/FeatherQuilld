# FeatherQuilld

FeatherQuilld is FeatherPanel's web hosting node daemon — light as a feather, sharp as a quill. It manages WebSpaces, reverse proxy (Caddy / nginx / Traefik), SFTP/FTP, and exposes an HTTP API for FeatherPanel.

The production daemon **runs as root**, same as FeatherWings: it talks to Docker, bind-mounts volumes, and owns `/etc/featherquilld` and `/var/lib/featherquilld`. Do not start it as a regular user — use `sudo quilld configure` then `systemctl`.

## Installation via APT (Debian/Ubuntu)

FeatherQuilld is available from the MythicalSystems APT repository in two channels:

| Package | Channel | When |
|---------|---------|------|
| `featherquilld` | **stable** | Published GitHub releases (`v*` tags) |
| `featherquilld-dev` | **nightly** | Every commit to `main` |

These packages conflict and cannot be installed at the same time. Switching channels (`apt install featherquilld` ↔ `featherquilld-dev`) replaces the other package automatically.

### Step-by-step

1. Import the MythicalSystems APT repository GPG key:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://apt.mythicalsystems.org/repository/keys/public.gpg \
  | sudo gpg --dearmor -o /etc/apt/keyrings/mythicalsystems.gpg
sudo chmod a+r /etc/apt/keyrings/mythicalsystems.gpg
```

2. Add the repository:

```bash
ARCH="$(dpkg --print-architecture)"
echo "deb [arch=${ARCH} signed-by=/etc/apt/keyrings/mythicalsystems.gpg] https://apt.mythicalsystems.org/repository/MythicalSystems/ stable main" \
  | sudo tee /etc/apt/sources.list.d/mythicalsystems.list
```

3. Install FeatherQuilld (stable):

```bash
sudo apt-get update
sudo apt-get install -y featherquilld
```

Or the nightly build:

```bash
sudo apt-get install -y featherquilld-dev
```

4. Configure and start (if the install wizard did not run automatically):

```bash
sudo featherquilld configure
sudo systemctl enable --now featherquilld
```

`quilld` is installed as a short alias for `featherquilld` (`sudo quilld configure` works the same).

### One-liner (stable)

```bash
sudo apt-get update && sudo apt-get install -y ca-certificates curl gnupg && \
  sudo install -d -m 0755 /etc/apt/keyrings && \
  curl -fsSL https://apt.mythicalsystems.org/repository/keys/public.gpg \
    | sudo gpg --dearmor -o /etc/apt/keyrings/mythicalsystems.gpg && \
  sudo chmod a+r /etc/apt/keyrings/mythicalsystems.gpg && \
  ARCH="$(dpkg --print-architecture)" && \
  echo "deb [arch=${ARCH} signed-by=/etc/apt/keyrings/mythicalsystems.gpg] https://apt.mythicalsystems.org/repository/MythicalSystems/ stable main" \
    | sudo tee /etc/apt/sources.list.d/mythicalsystems.list && \
  sudo apt-get update && sudo apt-get install -y featherquilld
```

### Upgrading

```bash
sudo apt-get update && sudo apt-get install --only-upgrade featherquilld
# or for nightly:
sudo apt-get update && sudo apt-get install --only-upgrade featherquilld-dev
```

---

## Configuration wizard

Run `featherquilld configure` (or `quilld configure`) to connect this machine to FeatherPanel as a web hosting node.

Three modes are available:

| Mode | How |
|------|-----|
| **OAuth quick setup** *(recommended)* | Opens a browser consent page on FeatherPanel; credentials and node registration are handled automatically |
| **Paste join-data** | Copy the base64 join-data string from Admin → Web Nodes → your node |
| **Manual credentials** | Enter `fqld_` token ID, secret, UUID, and panel URL directly |

### OAuth flags

```
featherquilld configure [flags]

  --panel-url <url>        FeatherPanel base URL (skips interactive prompt in OAuth mode)
  --callback-host <ip>     Public IP for the OAuth callback (auto-detected when omitted)
  --allow-insecure         Skip TLS verification for self-signed panel certificates
  --keep-oauth-key         Do not delete the temporary OAuth API key after node creation

  --join-data <base64>     Bootstrap directly from join-data (non-interactive)
  --override               Replace an existing config.yml
  --install-service        Install and enable the featherquilld systemd service
  --no-service             Skip systemd setup
  --quiet / -q             Suppress output (requires --join-data or --panel-url)
```

Non-interactive OAuth (e.g. cloud-init / CI):

```bash
sudo featherquilld configure \
  --panel-url https://panel.example.com \
  --callback-host 203.0.113.42 \
  --node-name "web-node-1" \
  --node-fqdn "web-node-1.example.com" \
  --location-id 2 \
  --install-service \
  --override
```

---

## Docker

```bash
docker compose up -d
```

Images are published to `ghcr.io/mythicalltd/featherquilld`:

| Tag | When |
|-----|------|
| `latest` | Stable release (non-prerelease) |
| `v1.2.3` | Specific release tag |
| `main` | Latest commit on main |
| `dev-<sha>` | Per-commit dev build |

---

## Development

```bash
make restore          # restore NuGet packages
make build            # Debug build
make run              # run daemon locally (reads config.yml)
make configure        # interactive configure wizard
make test             # run tests
make fmt              # format sources
make docker           # build Docker image
```

### Packaging locally

```bash
make deb              # build featherquilld_*.deb (prod, amd64 + arm64) → dist/
make deb-dev          # build featherquilld-dev_*.deb → dist/
make package          # build prod .deb and upload to Nexus if creds set
```

---

## Architecture

FeatherQuilld integrates with FeatherPanel via the `/api/quilld-remote/*` API prefix using a dedicated `fqld_` Bearer token separate from game node tokens. Configuration is two-phase:

1. **Join / bootstrap config** — minimal YAML passed via `--join-data` at install time
2. **Runtime config** — fetched from `GET /api/quilld-remote/config` on startup

Ports (defaults): HTTP API `8989` · SFTP `2222`

---

## Links

- [FeatherPanel](https://github.com/mythicalltd/featherpanel) — the panel this daemon integrates with
- [FeatherWings](https://github.com/mythicalltd/featherwings) — game server node daemon (same publish pipeline pattern)
- Issues: [github.com/mythicalltd/featherquilld/issues](https://github.com/mythicalltd/featherquilld/issues)
