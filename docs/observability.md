# Observability: health probes, Prometheus and Grafana

FeatherQuilld exposes:

| Endpoint | Auth | Purpose |
| --- | --- | --- |
| `GET /health/live` | none | Liveness: the process is up and serving HTTP. Always `200` while the daemon runs. |
| `GET /health/ready` | none (details need a token) | Readiness: the daemon and what it depends on are usable. `200` when healthy or degraded, `503` when unhealthy. |
| `GET /metrics` | bearer token (+ optional IP allowlist) | Prometheus metrics (text exposition format). |
| `GET /api/system/health` | node token | Legacy panel health probe, **unchanged**. |

## Readiness checks

`/health/ready` returns one entry per check plus an overall status (`healthy`, `degraded` or `unhealthy`; the worst check wins):

| Check | Unhealthy when | Degraded when |
| --- | --- | --- |
| `daemon` | the panel has put the node in maintenance mode | the last panel sync failed |
| `reverse_proxy` | the configured proxy (Caddy / nginx / Traefik) binary is missing or no process is running | the last reload failed (it may be serving an old config) |
| `docker` | Docker is unreachable **and** a WebSpace needs a container | Docker is unreachable, no container WebSpaces yet |
| `storage` | the WebSpace data directory is missing or not writable | |
| `disk_space` | free space on the data disk < `disk_critical_percent` (3%) | free space < `disk_warn_percent` (10%) |
| `disk_limiter` | | `fusequota` is the limiter but its binary is missing (quotas not enforced) |

Anonymous callers (load balancers, uptime monitors) only get names and statuses:

```json
{"status":"degraded","checks":{"daemon":{"status":"healthy"},"reverse_proxy":{"status":"healthy"},"docker":{"status":"degraded"},"storage":{"status":"healthy"},"disk_space":{"status":"healthy"},"disk_limiter":{"status":"healthy"}}}
```

Send `Authorization: Bearer <metrics token or node token>` (or set `public_details: true`) to also get each check's
description, timing and data (paths, free bytes, proxy provider, last reload, panel error, version).

## Configuration

In `/etc/featherquilld/config.yml`, under `api:`:

```yaml
api:
  metrics:
    enabled: true            # serve /metrics (a token is still required unless allow_anonymous)
    path: /metrics
    port: 0                  # 0 = main API port; e.g. 9464 = separate plain-HTTP listener for metrics + /health/*
    host: 127.0.0.1          # bind address of the separate listener (0.0.0.0 / private IP for remote Prometheus)
    token: ""                # scrape-only token; the node token also works. Generate one: openssl rand -hex 32
    allow_anonymous: false   # skip the token check (only with allowed_ips or a loopback listener)
    allowed_ips: []          # e.g. [10.0.0.5, 10.10.0.0/16]; empty = any address
    per_webspace: true       # per-WebSpace series (labelled by uuid); turn off on very large nodes
  health:
    enabled: true
    public_details: false
    disk_warn_percent: 10
    disk_critical_percent: 3
```

### How `/metrics` is protected

* A bearer token is required by default: the dedicated `token` (recommended, so Prometheus never holds the full-access
  node token) or the node token. Tokens are only read from the `Authorization` header, never from the query string.
  Wrong / missing token: `401`. The scrape token grants nothing else: every `/api/*` route still needs the node token.
* `allowed_ips` (IPs or CIDRs) is checked first, even with a valid token: other addresses get `403`. Behind the local
  reverse proxy the client address comes from `X-Forwarded-For` (only trusted from loopback).
* With `port` set, metrics are only served on that listener (`404` on the API port) and that listener answers nothing
  but `/metrics`, `/health/live` and `/health/ready`, so it can be firewalled separately from the panel API.
  It is plain HTTP; keep it on a private network / `host: 127.0.0.1` or behind a TLS proxy.
* `enabled: false` removes the endpoint entirely.

## Metrics

Besides the standard `process_*` and `dotnet_*` runtime metrics and `http_request*` API metrics (by controller, action,
endpoint and status code), the daemon exports:

| Metric | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `featherquilld_build_info` | gauge | `version`, `node` | always 1 |
| `featherquilld_uptime_seconds` | gauge | | daemon uptime |
| `featherquilld_maintenance_mode` | gauge | | 1 in maintenance mode |
| `featherquilld_panel_reachable` | gauge | | 1 when the last panel sync worked |
| `featherquilld_webspaces` | gauge | | WebSpaces on the node |
| `featherquilld_webspaces_by_state` | gauge | `state` | running / stopped / starting / stopping |
| `featherquilld_webspaces_by_status` | gauge | `status` | installed / installing / reinstalling / failed |
| `featherquilld_webspaces_suspended` | gauge | | suspended WebSpaces |
| `featherquilld_webspaces_waf_enabled` | gauge | | WebSpaces with the firewall on |
| `featherquilld_domains` | gauge | | routed domains |
| `featherquilld_webspace_info` | gauge | `webspace`, `name`, `runtime`, `state`, `status` | always 1 (join on `webspace`) |
| `featherquilld_webspace_up` | gauge | `webspace` | 1 when running |
| `featherquilld_webspace_suspended`, `_waf_enabled`, `_domains` | gauge | `webspace` | per WebSpace |
| `featherquilld_webspace_disk_limit_bytes`, `_memory_limit_bytes`, `_cpu_limit` | gauge | `webspace` | limits (0 = unlimited) |
| `featherquilld_webspace_bandwidth_used_bytes`, `_bandwidth_limit_bytes` | gauge | `webspace` | bandwidth this period |
| `featherquilld_proxy_enabled`, `featherquilld_proxy_running` | gauge | `provider` | proxy managed / process running |
| `featherquilld_proxy_reloads_total` | counter | `provider`, `action` (`reload` / `start`), `result` | proxy reload / start commands |
| `featherquilld_proxy_reload_duration_seconds` | histogram | `provider`, `action` | reload command duration |
| `featherquilld_proxy_last_reload_success` | gauge | `provider` | 1 when the last reload worked |
| `featherquilld_proxy_last_reload_timestamp_seconds` | gauge | `provider` | time of the last reload |
| `featherquilld_proxy_config_rebuilds_total` | counter | `provider`, `result` | proxy config regenerations |
| `featherquilld_waf_events_total` | counter | `webspace`, `category`, `action` (`blocked` / `detected`) | firewall events recorded since start |
| `featherquilld_sftp_auth_failures_total` | counter | | failed SFTP logins |
| `featherquilld_sftp_lockouts_total` | counter | | SFTP brute-force lockouts |
| `featherquilld_host_cpu_percent`, `_cpu_threads` | gauge | | host CPU |
| `featherquilld_host_memory_total_bytes`, `_used_bytes`, `featherquilld_host_swap_*` | gauge | | host memory / swap |
| `featherquilld_host_load` | gauge | `window` (`1m`, `5m`, `15m`) | load average |
| `featherquilld_data_disk_total_bytes`, `_free_bytes` | gauge | `path` | disk holding WebSpace data |

Firewall events are counted when the daemon folds new access / audit log lines into a WebSpace's firewall history
(the same data as the panel's firewall dashboard), so they show up with that delay.

## Prometheus

1. Set a scrape token and (optionally) a dedicated port, then restart: `systemctl restart featherquilld`.
2. Put the token in a file only Prometheus can read, e.g. `/etc/prometheus/featherquilld.token`.
3. Add a job like [`contrib/prometheus/prometheus.yml`](../contrib/prometheus/prometheus.yml):

```yaml
scrape_configs:
  - job_name: featherquilld
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials_file: /etc/prometheus/featherquilld.token
    static_configs:
      - targets: ['node1.example.com:9464']
```

Example alerts (proxy down, reload failing, panel unreachable, disk low, failed installs, firewall spikes, SFTP brute
force) are in [`contrib/prometheus/featherquilld-alerts.yml`](../contrib/prometheus/featherquilld-alerts.yml).

Quick check from the node:

```sh
curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9464/metrics | grep ^featherquilld_
curl -s http://127.0.0.1:9464/health/ready
```

## Grafana

Import [`contrib/grafana/featherquilld.json`](../contrib/grafana/featherquilld.json) (Dashboards → New → Import) and pick
your Prometheus data source. It has node status, WebSpaces, reverse proxy & firewall, host and daemon runtime rows,
filterable by `job` and `instance`.

## Kubernetes / Docker probes

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8989 }
readinessProbe:
  httpGet: { path: /health/ready, port: 8989 }
```

```yaml
# compose
healthcheck:
  test: ["CMD", "curl", "-fsS", "http://127.0.0.1:8989/health/ready"]
  interval: 30s
```
