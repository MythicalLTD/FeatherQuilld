# Security fixes 2026-10-14 — FeatherQuilld

This branch carries the **description and the patch**; the code branch itself could not be
pushed yet because the file `.github/workflows/release.yml` (and upstream workflow commits
between the fork's `master` and upstream `master`) require the GitHub token scope
`workflow`, which the current token does not have:

```
gh auth refresh -s workflow
```

After that, the code branch `security-fixes-2026-10-14` (16 files, base commit `e116e39`)
is pushed as usual. Until then the content lives here:

| File | Content |
|---|---|
| `SECURITY-FIXES-2026-10-14.md` | the complete description: every finding, the file/line, the fix and the behaviour impact |
| `security-fixes-2026-10-14.patch` | the code patch (16 files, without `.github/workflows/release.yml`) — apply with `git apply --3way security-fixes/security-fixes-2026-10-14.patch` on `e116e39` |
| `release-workflow.patch` | the one-line CI hardening (tag values via `env:` instead of shell interpolation) that needs the `workflow` scope |

## Summary

* `?token=` is only accepted on WebSocket routes, and `token=<value>` is masked in the
  request log (the node-wide token used to end up in logs and proxy logs).
* `FallbackPolicy = RequireAuthenticatedUser` — `[AllowAnonymous]` only on the landing
  page, the WebSocket upgrade and the signed upload route.
* Four path traversals from panel-supplied values closed (DKIM lookup, autoresponder
  sieve path, spam filter state, proxy log file name) using a whitelist regex plus
  `RootedPath.IsUnderRoot`.
* `document_root` for the generated Apache config restricted to `[A-Za-z0-9._/-]`.
* `fusequota` binary pinned to tag `a39bc56` with SHA-256 verification of all four assets
  (hashes verified against the published release assets) before it is executed as root.
* Restore skips symlink/hardlink/device/FIFO entries instead of aborting.

Verification: `dotnet build -c Release` (0 warnings, 0 errors), `dotnet test`
(515 passed, 0 failed), patch applies cleanly on a fresh clone of `e116e39`.

Deliberately not changed: one bearer token for every action (needs a simultaneous panel
change and would break the running protocol), and the SHA-1 backup id compatibility is not
an issue here.
