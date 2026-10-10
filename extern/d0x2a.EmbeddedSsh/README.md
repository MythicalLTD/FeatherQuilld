# EmbeddedSsh

A minimal, embeddable SSH-2 server library for .NET 10.0 following RFC 4251-4254.

## Features

- **Zero external dependencies** - All cryptographic primitives implemented from scratch
- **Pure managed code** - No native libraries or P/Invoke
- **RFC compliant** - Implements SSH-2 protocol (RFC 4251-4254)
- **Modern cryptography**:
  - Post-quantum hybrid key exchange (mlkem768x25519-sha256)
  - ChaCha20-Poly1305 and AES-256-GCM ciphers
  - Curve25519 key exchange
  - Ed25519 host keys
  - RSA and Ed25519 public key authentication

## Supported Algorithms

| Type | Algorithms |
|------|------------|
| Key Exchange | mlkem768x25519-sha256, curve25519-sha256, curve25519-sha256@libssh.org |
| Host Key | ssh-ed25519 |
| Cipher | chacha20-poly1305@openssh.com, aes256-gcm@openssh.com |
| Authentication | publickey (ssh-ed25519, ssh-rsa, rsa-sha2-256, rsa-sha2-512), password |

## Installation

```bash
dotnet add package d0x2a.EmbeddedSsh
```

## Quick Start

```csharp
using System.Net;
using d0x2a.EmbeddedSsh;
using d0x2a.EmbeddedSsh.Auth;
using d0x2a.EmbeddedSsh.HostKeys;

// Generate or load host key
var hostKey = Ed25519HostKey.Generate();

// Configure server
var options = new SshServerOptions
{
    ServerVersion = "SSH-2.0-MyServer",
    Authenticator = new PasswordAuthenticator(new Dictionary<string, string>
    {
        ["user"] = "password"
    })
};
options.HostKeys.Add(hostKey);

// Start server
await using var server = new SshServer(options, new IPEndPoint(IPAddress.Any, 2222));

server.ConnectionAccepted += connection =>
{
    _ = Task.Run(async () =>
    {
        var channel = await connection.AcceptChannelAsync();
        // Handle channel...
    });
    return Task.CompletedTask;
};

server.Start();
await Task.Delay(Timeout.Infinite);
```

## Public Key Authentication

Use `AuthorizedKeysAuthenticator` for SSH key authentication:

```csharp
// From authorized_keys file
var content = File.ReadAllText("~/.ssh/authorized_keys");
var keys = AuthorizedKeysAuthenticator.ParseAuthorizedKeysFile(content);

var options = new SshServerOptions
{
    Authenticator = new AuthorizedKeysAuthenticator(
        new Dictionary<string, IEnumerable<AuthorizedKey>>
        {
            ["username"] = keys
        })
};
```

## Building

```bash
# Build
dotnet build

# Run tests
dotnet test

# Create NuGet package
dotnet pack -c Release

# Run example server
cd samples/EmbeddedSsh.Example
dotnet run
```

## Architecture

The library follows the SSH RFC layered architecture:

```
+-----------------------------------------+
|             SshServer                   |  Server integration
+-----------------------------------------+
|           Connection Layer              |  Channels, requests
+-----------------------------------------+
|             Auth Layer                  |  Authentication
+-----------------------------------------+
|           Transport Layer               |  Encryption, framing
+-----------------------------------------+
|            Protocol Layer               |  Message serialization
+-----------------------------------------+
|             Crypto Layer                |  Primitives
+-----------------------------------------+
```

## Requirements

- .NET 10.0

## License

MIT