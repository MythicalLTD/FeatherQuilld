# Mail backends (docker-mailserver and mailcow)

FeatherQuilld drives one mail stack per node. Which one is a configuration value, not a
build decision:

| Backend | `system.mail.backend` | Notes |
|---------|----------------------|-------|
| docker-mailserver (default) | `docker-mailserver` (aliases `docker`, `docker-mail-server`, `dms`) | One container, driven through the image's `setup` CLI. Existing installations keep exactly this behaviour. |
| mailcow: dockerized | `mailcow` (aliases `mailcow-dockerized`, `mailcowdockerized`) | Full stack (~15 containers) driven through mailcow's REST API — either installed by FeatherQuilld on this node or running on another host. |

An unknown value is rejected (`Unsupported mail backend '…'. Valid values for
system.mail.backend: …`) instead of silently managing the other stack. `/api/mail/*` is
identical for both backends, so the panel and its WebSpace DNS provisioning do not care which
stack answers.

## 1. Enable mailcow on this node (FeatherQuilld installs and manages it)

**From the panel:** open the web node, tab *Package manager*, and install the
`Mail server (mailcow: dockerized)` package — the panel registers the node's mail host for you and
its card links back here. The backend switch itself is a node setting: paste the three lines under
*Configuration → Advanced config overrides* (`system.mail.backend: mailcow`, plus
`mail.mailcow.url`/`api_key` only when the stack runs somewhere else). The commands below are the
same steps without a panel.

Requirements on the mail host:

* Docker with the compose plugin (`docker compose version` must work) — mailcow is not a
  single container.
* `git`, `curl` and `tar` on `PATH` (the package fetches the mailcow release tarball).
* ~4 GiB RAM free. Below 2.5 GiB the installer disables ClamAV (`SKIP_CLAMD=y`), the answer
  mailcow's own installer would ask for.
* Public IP with a resolvable mail hostname, inbound **25** (MX) plus the submission/IMAP
  ports you configure, and a **PTR/rDNS** record pointing at that hostname. Outbound 25 is
  what most receivers check first; many providers block it on VPS plans.
* **80/443 stay with the panel** (Traefik/nginx terminates the sites and can proxy the mailcow
  UI). mailcow therefore listens on loopback only: `HTTP_BIND/HTTPS_BIND=127.0.0.1` with
  `http_port`/`https_port` below.

Steps:

1. Point the node at mailcow — minimal config (everything else has a working default):

   ```yaml
   system:
     mail:
       enabled: true
       backend: mailcow
       hostname: mail.example.com        # MX target; must resolve and match your PTR
       smtp_port: 587
       imap_port: 993
       dkim_selector: mail
       mailcow:
         # path defaults to <mail data path>/mailcow, url to https://<hostname>:<https_port>
         url: ""                         # empty = the stack installed on this node
         api_key: ""                     # empty = read the feather-api-key file next to the compose file
         http_port: 8080
         https_port: 8443
         skip_acme: true                 # certificates come from the panel's reverse proxy
         mail_host: ""                   # only for a mailcow on another host, see section 2
         domain_mailbox_limit: 10        # mailbox slots per domain created through the API
         insecure_tls: false             # true while mailcow serves its self-signed default cert
   ```

2. Install the stack (host package `mailcow`, from the panel's package page or directly):

   ```bash
   curl -fsS -X POST -H "Authorization: Bearer <node-token>" \
     https://127.0.0.1:8989/api/system/packages/mailcow/install
   ```

   `POST /api/system/packages/mailserver/install` is the docker-mailserver equivalent;
   `GET /api/system/packages` lists both with their status, `POST …/{id}/remove?purge_config=true`
   removes a stack including its data.

3. Verify before touching DNS:

   ```bash
   curl -fsS -H "Authorization: Bearer <node-token>" https://127.0.0.1:8989/api/mail/probe
   ```

   The payload must show `backend: mailcow`, `mode: local`, `api_key_configured: true`,
   `api_url`, `container`, and `submission_open`/`imap_open: true`. `available: false` together
   with `port_25_open: false` means inbound SMTP is blocked — fix that before creating mailboxes.

4. Publish the mail DNS records. The daemon computes them per domain
   (`GET /api/mail/dns-hints/<domain>`): `MX @` → `mx_host`, `TXT @` →
   `v=spf1 mx a:<mailhost> -all`, `TXT <selector>._domainkey` (DKIM, read from mailcow through
   its API) and `TXT _dmarc`. Autodiscover/autoconfig CNAMEs and the `SRV _imaps/_submission/_pop3s`
   records are optional but make Thunderbird/Apple Mail configure themselves. Check
   `mx_host` resolves (`dig +short <mx_host>`) and the sending IP has a matching PTR
   (`dig -x <ip>`) — correct SPF/DKIM/DMARC still deliver badly without it.

What the `mailcow` package writes: mailcow owns its configuration, so the installer runs
mailcow's own `generate_config.sh` non-interactively (`.env` is a symlink to `mailcow.conf` and
`mailcow.conf.example` no longer exists upstream) and then overrides only the keys FeatherQuilld
owns: `MAILCOW_HOSTNAME`, `TZ`, `HTTP_PORT`/`HTTPS_PORT` with loopback binds, `SKIP_LETS_ENCRYPT`
(per `skip_acme`), `SKIP_CLAMD` on small hosts, `API_KEY`, `API_KEY_READ_ONLY`, `API_ALLOW_FROM`
and `SKIP_IP_CHECK=y` (the API is reached through the published loopback port, where Docker
rewrites the source IP to the bridge gateway; the key stays the guard, the port stays private).
Nothing has to be pasted into the mailcow UI: the same write key is written to `feather-api-key`
next to the compose file and used by the panel. If a checkout has no `generate_config.sh`, a
documented fallback config is written instead.

## 2. Use a mailcow that runs on another host

```yaml
system:
  mail:
    enabled: true
    backend: mailcow
    hostname: mail.example.com        # what MX records should point at
    mailcow:
      url: https://mail.other-host.example.com   # the mailcow UI/API you already run
      api_key: "<write API key>"                 # or leave empty to read <mailcow.path>/feather-api-key (mode 600)
      mail_host: mail.other-host.example.com     # MX host when it differs from `hostname`
      insecure_tls: false                        # true if that mailcow still uses its self-signed cert
```

* The daemon accepts the remote stack when **either** the API answers ping
  (`GET /api/v1/get/status/version`) **or** local containers exist; `/api/mail/probe` then reports
  `mode: remote` instead of pretending the stack is missing. Port checks run against that host,
  not against loopback.
* mailcow must allow this node: `api.allow_from` in its database (or `API_ALLOW_FROM` in
  `mailcow.conf`) has to contain the node's egress IP, otherwise a valid key still answers
  `401 api access denied for ip …`. `skip_ip_check=1` disables that check — only for setups where
  the API is not reachable from the outside at all.
* Autoresponders (vacation messages) stay **local-only**: they are installed with
  `doveadm sieve` inside the dovecot container, which needs shell access to that mailcow host.
  Everything else — domains, mailboxes, passwords, enable/disable, aliases, spam score, DKIM —
  works remotely.
* `mail.hostname` (or `mail.mailcow.mail_host`) must be set on a remote setup; otherwise the DNS
  hints would publish `MX mail.<domain>`, a name that does not exist.

## Configuration reference (`system.mail.*`)

| Key | Default | Meaning |
|-----|---------|---------|
| `enabled` | `true` | Master switch for the mail API and self-test. |
| `backend` | `docker-mailserver` | `docker-mailserver` or `mailcow` (see aliases above). |
| `hostname` | `""` | Public mail hostname used for MX/SPF, e.g. `mail.example.com`. |
| `webmail_hostname` | `""` | Hostname of the node Roundcube (proxied HTTPS). |
| `data_path` | `""` | Mail data root; empty uses `<node root>/mail`. mailcow lives in `<data root>/mailcow`. |
| `smtp_port` / `imap_port` | `587` / `993` | Ports published in the DNS hints and checked by the probe. |
| `dkim_selector` | `mail` | Selector used when asking the backend for a DKIM key. |
| `mailcow.path` | `""` | Directory with mailcow's compose file and `data/` (empty = `<mail data path>/mailcow`). |
| `mailcow.url` | `""` | API base URL; empty derives `https://<hostname>:<https_port>`. |
| `mailcow.api_key` | `""` | Write API key; empty reads `feather-api-key` next to the compose file. |
| `mailcow.http_port` / `mailcow.https_port` | `8080` / `8443` | Loopback ports for the mailcow UI/API. Ports are policy - agree them with the operator. |
| `mailcow.skip_acme` | `true` | Skip mailcow's ACME client because the panel's proxy owns 80/443. |
| `mailcow.mail_host` | `""` | MX host for a mailcow on another host (falls back to `hostname`). |
| `mailcow.domain_mailbox_limit` | `10` | Mailbox slots for domains created through the API. mailcow rejects every mailbox while this is `0` (`max_mailbox_exceeded`). |
| `mailcow.insecure_tls` | `false` | Accept mailcow's self-signed default certificate (first start, before a real certificate exists). |

Shipped mail API surface (identical for both backends):
`GET /api/mail/probe`, `GET|POST /api/mail/domains`, `DELETE /api/mail/domains/{name}`,
`POST /api/mail/provision` (`create`, `delete`, `reset_password`, `set_enabled`, `set_forward`,
`delete_forward`, `set_autorespond`, `set_spam_filter`, `create_list`, `delete_list`,
`set_list_member`), `GET /api/mail/dns-hints/{domain}`, `/api/mail/deliverability`,
`/api/mail/lists`, `…/mailboxes/{email}/spam`, `/api/mail/webmail`,
`POST /api/mail/webmail/configure`.

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `mailcow: dockerized mail server is not running.` with a remote `url` in the message | The API did not answer. Check `mailcow.api_key` and that the node's IP is in `API_ALLOW_FROM`; a mailcow on another host additionally needs ports 25/`smtp_port`/`imap_port` reachable from here. |
| `Unsupported mail backend 'x'` | Typo in `backend`. Valid values are listed in the message. |
| `401 api access denied for ip …` | Node IP missing in mailcow's `api.allow_from`; extend it (`UPDATE api SET allow_from = CONCAT(allow_from, ',<ip>')` or `API_ALLOW_FROM` in `mailcow.conf`). |
| TLS errors against `https://127.0.0.1:8443` | mailcow's self-signed default certificate: set `mailcow.insecure_tls: true`, or terminate a real certificate through the panel's proxy and point `mailcow.url` at it. |
| `max_mailbox_exceeded; 0; 0` when creating a mailbox | The domain was created with `mailboxes: 0` — raise `mailcow.domain_mailbox_limit` (default 10) and recreate/update the domain. |
| `available: false` in `/api/mail/probe`, `port_25_open: false` | Inbound 25 blocked (host firewall or provider). Without it mail is stored but never accepted; the probe's `deliverability_hint` says so. |
| Mailboxes are created but nothing arrives | MX not published or pointing at a name that does not resolve — re-check `GET /api/mail/dns-hints/<domain>`, then `dig`/`dig -x` from outside. |
| Containers restart in a loop after install | Look at `docker compose logs` in the mailcow directory; a `mailcow.conf` that was never generated (missing `.env` symlink) is the classic one. |

## For contributors

* `Utils/Mail/MailBackendKind.cs` holds the accepted values, `MailBackendFactory` the selection
  (unknown values are rejected, `Normalize` stays lenient for probes/diagnostics).
* `IMailBackend` (`Utils/Mail/Backends/`) is the seam: `MailManager` keeps payload parsing, event
  hooks and bookkeeping, backends do the stack-specific work. `NotRunningHint()` has a default
  implementation, so adding a backend does not break other implementers or test fakes.
* mailcow specifics live in `Utils/Mail/Mailcow/` (API client, compose/conf handling, `doveadm`
  Sieve for autoresponders); paths in `MailcowPaths.cs`.
* Tests: `FeatherQuilld.Tests/Mail/` (`MailBackendSelectionTests`, `MailcowBackendTests`,
  `MailcowConfigTests`, `MailProbeTests`). mailcow payloads are asserted against a fake
  `IMailcowApi`; anything that needs a live stack is marked in the test name.
