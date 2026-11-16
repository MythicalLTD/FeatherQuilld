# FeatherQuilld

FeatherQuilld is FeatherPanel's web hosting node daemon light as a feather, sharp as a quill. It manages WebSpaces, reverse proxy (Caddy / nginx / Traefik), SFTP/FTP, and exposes an HTTP API for FeatherPanel.

Mail stacks (docker-mailserver or mailcow) are documented in [docs/mail-backends.md](docs/mail-backends.md).

Health probes (`/health/live`, `/health/ready`), the Prometheus `/metrics` endpoint and the Grafana dashboard are documented in [docs/observability.md](docs/observability.md).
