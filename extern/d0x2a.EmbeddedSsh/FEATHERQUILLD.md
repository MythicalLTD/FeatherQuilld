# Vendored EmbeddedSsh (FeatherQuilld)

Upstream: https://github.com/d0x2a/EmbeddedSsh (MIT), based on 1.1.0.

Local 1.1.2 changes:
- `SshServerOptions.ChannelRequestHandler` wired into `ConnectionLayer` at construction
- Auth ignore/debug handling; channel reply id fix; strict-KEX; auth lockout / channel cap /
  packet clamp / password timing; constant-time Ed25519 scalar mult
