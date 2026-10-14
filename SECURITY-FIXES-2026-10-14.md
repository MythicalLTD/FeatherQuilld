# Security fixes — FeatherQuilld (branch `security-fixes-2026-10-14`)

Base commit: `e116e39` (upstream `master`). 16 files changed, no new NuGet packages,
no schema change, no new mandatory configuration. The panel protocol is unchanged.

## FQ-1 — Node bearer token in query strings and logs

* `Middleware/BearerTokenAuthenticationHandler.cs` accepted `?token=` on **every**
  endpoint, while `Middleware/RequestResponseLoggingMiddleware.cs` logged path **and** query
  string — the node-wide token ended up in logs, proxy logs and shell histories.
* Now the query token is only accepted when the last path segment is `ws`
  (`/api/webspaces/{uuid}/ws`, `/api/system/packages/ws`), and the logged line masks
  `token=<value>` as `token=***`.

## FQ-2 — Authorization was opt-in per controller

* `Utils/Startup/DaemonHost.cs` called `AddAuthorization()` without a `FallbackPolicy`, so
  every new controller/action was public unless someone remembered `[Authorize]`
  (`GET /` already was).
* Now the fallback policy is `RequireAuthenticatedUser`. The three endpoints that must stay
  anonymous carry `[AllowAnonymous]` explicitly and were verified: `GET /`
  (landing page), `GET /api/webspaces/{uuid}/ws` (browser WebSocket upgrade — auth happens
  after the upgrade via the JWT event), `POST .../files/upload-signed` (own HMAC token via
  `WebSpaceUploadToken.TryValidate`). `/metrics` and the plugin routes keep their existing
  `RequireAuthorization()`. `/openapi.json` + `/scalar` now require the bearer when
  `api.docs.enabled=true` (default off).

## FQ-3 — One token for everything (documented, not changed)

* The identity has no roles/scopes; the bearer may trigger self-update, package installs,
  plugin deletion and wipes. A real fix needs the panel to issue purpose-scoped tokens at
  the same time, i.e. a protocol change on both sides — documented here as deliberately
  open instead of breaking the running installation.

## FQ-5/FQ-7/FQ-8/FQ-9 — Path traversal from panel-supplied values

Four places interpolated request values into filesystem paths under the root account:

| Location | Value | Fix |
|---|---|---|
| `Utils/Mail/MailDnsHelper.cs` (`TryGetDkimRecord`) with `Controllers/MailController.cs` (`GET /api/mail/dns-hints/{domain}`) | route `domain` | validated with `WebSpaceValidation.IsValidDomain`, otherwise no DKIM lookup (hints without DKIM instead of an error) |
| `Utils/Mail/MailVacationHelper.cs` (`ConfigSievePath`, `MaildirSievePaths`) | mailbox address from `POST /api/mail/provision` (`set_autorespond`) | new `MailAddressRules` whitelist plus `RootedPath.IsUnderRoot` against the config dir; invalid address = no-op |
| `Utils/Mail/MailSpamHelper.cs` (`StatePath` — only replaced `@` before) | address from `set_spam_filter` | same validation; invalid address writes nothing, read returns the default |
| `Utils/Proxy/ProxyAccessLogs.cs` (`AccessLogPath`/`ErrorLogPath`/`RotateSpace`) via `Utils/WebSpaces/WebSpaceStore.cs` and `Controllers/WebSpacesController.cs` | `?domain=` on `/proxy-logs` and `/proxy-logs/rotate` | checked against `WebSpaceValidation.IsValidDomain` **and** `space.Domains`, otherwise HTTP 400 (empty parameter still means "all domains of the space") |

## FQ-10 — Apache config injection (`Utils/WebSpaces/WebSpaceSiteFiles.cs`)

* The panel-supplied `document_root` (previously only stripped of `.`/`..`) was written
  unescaped into `DocumentRoot`/`<Directory>` directives, so a newline or quote could inject
  directives into the PHP container. Now only `[A-Za-z0-9._/-]` is allowed (no empty, `.` or
  `..` segments, no control characters/quotes); invalid values fall back to `/var/www/html`.

## FQ-11 — Unverified binary download (`Utils/WebSpaces/Disk/FuseQuotaBinaryProvisioner.cs`)

* `fusequota` was fetched from `releases/latest` (overridable by environment), installed and
  executed as root without any integrity check.
* Now the release is pinned (`DefaultReleaseTag = a39bc56`) and the SHA-256 of all four
  assets is verified before install (mismatch aborts). The hashes were computed from the
  published release assets. A custom `FUSEQUOTA_RELEASE_BASE` still skips verification —
  the pin has to be bumped when upstream publishes a new release.

## FQ-13 — Shell injection in the release workflow

* `.github/workflows/release.yml` interpolated `github.event.release.tag_name` and the
  workflow input directly into a `run:` step. The values are now passed through `env:` and
  quoted in the shell; no `${{ }}` remains inside the shell block.

## Additional hardening

* `WebSpaceBackupService.ExtractTarGz` uses a `TarReader` loop and skips
  symlink/hardlink/device/FIFO/PAX entries instead of aborting the restore with an
  exception (defence in depth; the previous code was already protected by the BCL).
* New regression tests: `FeatherQuilld.Tests/Security/SecurityHardeningTests.cs` (23 tests).

## Verification

* `dotnet build FeatherQuilld.csproj -c Release` → Build succeeded, 0 warnings, 0 errors.
* `dotnet test FeatherQuilld.Tests/FeatherQuilld.Tests.csproj` → 515 passed, 0 failed.
* Patch applies cleanly on a fresh clone of `e116e39` (`git apply --check --3way`, 16 files).
* The pinned fusequota hash was re-verified against the published release asset
  (`fusequota-x86_64-linux`, SHA-256 `b339e7d8…9992`).

## Intentionally not changed

* **FQ-3** scope separation (needs a simultaneous panel change, see above).
* `ServerName` in the generated Apache config keeps using the raw domain: domains are
  validated by the panel when a WebSpace is created.
* `WebSpaceInstaller.cs:51` (`Path.Combine(dataPath, …)`, flagged by Semgrep) is not
  user-controlled — `dataPath` is derived from the WebSpace UUID.
