# Vendored EmbeddedSsh (FeatherQuilld)

Upstream: https://github.com/d0x2a/EmbeddedSsh (MIT), based on 1.1.0.

## Local delta (1.1.1)

`SshServerOptions.ChannelRequestHandler` is passed into `ConnectionLayer` at
construction time from `SshConnection.RunAsync`. That removes the post-auth race
where OpenSSH clients pipeline `channel-open` + `subsystem:sftp` before a
late `ChannelRequestReceived` subscriber can attach
(`subsystem request failed on channel 0`).
