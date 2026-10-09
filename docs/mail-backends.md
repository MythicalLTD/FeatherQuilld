# Mail backends (docker-mailserver and mailcow)

`system.mail.backend` selects the stack FeatherQuilld manages:

| Backend | Value | Notes |
|---------|-------|-------|
| docker-mailserver (default) | `docker-mailserver` | One container, driven through its `setup` CLI. Existing installations keep this behaviour. |
| mailcow: dockerized | `mailcow` | Full stack (~15 containers), driven through mailcow's REST API. |

An unknown value is rejected instead of silently managing the other stack.

```yaml
system:
  mail:
    enabled: true
    backend: mailcow          # or docker-mailserver
    hostname: mail.example.com
    mailcow:
      path: ""                 # defaults to <mail data path>/mailcow
      url: ""                  # defaults to https://<hostname>:<https_port>
      api_key: ""              # defaults to the feather-api-key file next to the compose file
      http_port: 8080          # mailcow UI HTTP port (80/443 stay with the panel's proxy)
      https_port: 8443
      skip_acme: true          # certificates come from the panel's reverse proxy
      insecure_tls: false      # set true while mailcow serves its self-signed default cert
```

Install/remove the stack from the host package manager (`mailserver` for docker-mailserver,
`mailcow` for mailcow). `/api/mail/*` is identical for both backends:

* domains, mailboxes, passwords, enable/disable and aliases map 1:1
* the per-mailbox spam filter is a rspamd bypass entry (docker-mailserver) or a spam score (mailcow)
* autoresponders are Sieve scripts — written into the mailbox directory (docker-mailserver) or
  activated per mailbox with `doveadm sieve` inside the dovecot container (mailcow)
* mailing lists are emulated with per-member aliases, because neither stack exposes list objects

## What the mailcow package install does to `mailcow.conf`

mailcow owns its configuration file: upstream removed `mailcow.conf.example` and turned `.env`
into a symlink to `mailcow.conf`, so the panel runs mailcow's own `generate_config.sh`
(non-interactively: hostname, timezone and the ClamAV answer are passed in, `--dev` keeps it from
checking out a branch) and only then overrides the keys it owns:

* `MAILCOW_HOSTNAME`, `TZ`
* `HTTP_PORT` / `HTTPS_PORT` plus `HTTP_BIND=127.0.0.1` / `HTTPS_BIND=127.0.0.1` — the panel's
  reverse proxy keeps 80/443, so the mailcow UI/API is published on loopback only
* `SKIP_LETS_ENCRYPT` (per `mail.mailcow.skip_acme`)
* `SKIP_CLAMD=y` on hosts with ≤ 2.5 GiB RAM, the answer mailcow's installer would ask for
* `API_KEY`, `API_KEY_READ_ONLY`, `API_ALLOW_FROM` — the panel generates the key, so nothing has
  to be pasted into the mailcow UI; the same write key is written to `feather-api-key` for the
  panel's own client
* `SKIP_IP_CHECK=y`: the API is reached through the published loopback port, where docker
  rewrites the source IP to the bridge gateway. The key stays the guard, the port stays private.

If a checkout has no `generate_config.sh`, a documented fallback file is written instead of an
empty one (the previous behaviour parsed `mailcow.conf.example`, which no longer exists, and
produced a mailcow that could not start).

## DKIM and DNS hints

Current mailcow releases keep DKIM keys in redis, not as files, so the DNS hints read the public
record through `GET /api/v1/get/dkim/<domain>` (`dkim_selector`, `dkim_txt`, chunked values are
joined). The old `data/dkim/<domain>/<selector>.txt` candidates stay as a fallback for old
checkouts and half-migrated hosts. `GET /api/mail/dns-hints/<domain>` therefore returns the DKIM
TXT for the mailcow backend too, which is what the panel's WebSpace DNS provisioning (MX/SPF/DKIM
via `DnsProvisioner::provisionMailRecords`) consumes.
