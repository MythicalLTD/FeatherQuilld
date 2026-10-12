# fqctl

Tiny privileged filesystem helper for FeatherQuilld. Replaces ad-hoc
`umount -l` / `chown -R` shells with stable argv and exit codes so the
daemon can later drop full root and keep only this binary setuid /
capability-scoped.

## Commands

| Command | Meaning |
|---|---|
| `fqctl umount-lazy <path>` | `umount2(MNT_DETACH)`; already-gone mounts are success |
| `fqctl chown-r <uid:gid> <path>` | Recursive `lchown` (numeric ids only, no symlink follow) |
| `fqctl mount-bind <src> <dst>` | `mount(MS_BIND)` |
| `fqctl version` | Print version |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | usage / bad arguments |
| 2 | path not found / invalid |
| 3 | permission denied |
| 4 | busy / in use |
| 5 | other OS error |

## Build

```bash
make -C native/fqctl install   # → bins/fqctl
# or from repo root:
make build-fqctl
```

Override path with `system.fqctl_path` or `FQCTL_BIN`. When missing, FeatherQuilld
falls back to system `umount` / `chown`.
