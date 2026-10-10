namespace d0x2a.EmbeddedSsh.Protocol.Messages;

/// <summary>
/// SSH_MSG_USERAUTH_REQUEST (RFC 4252 §5)
/// </summary>
public sealed class UserauthRequestMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.UserauthRequest;

    public required string Username { get; init; }
    public required string ServiceName { get; init; }
    public required string MethodName { get; init; }

    /// <summary>
    /// For publickey method: algorithm name.
    /// </summary>
    public string? PublicKeyAlgorithm { get; init; }

    /// <summary>
    /// For publickey method: public key blob.
    /// </summary>
    public ReadOnlyMemory<byte>? PublicKeyBlob { get; init; }

    /// <summary>
    /// For publickey method: whether this is a signature request (true) or query (false).
    /// </summary>
    public bool HasSignature { get; init; }

    /// <summary>
    /// For publickey method with signature: the signature.
    /// </summary>
    public ReadOnlyMemory<byte>? Signature { get; init; }

    /// <summary>
    /// For password method: the password.
    /// </summary>
    public string? Password { get; init; }

    public static UserauthRequestMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);

        var username = reader.ReadString();
        var serviceName = reader.ReadString();
        var methodName = reader.ReadString();

        string? publicKeyAlgorithm = null;
        ReadOnlyMemory<byte>? publicKeyBlob = null;
        bool hasSignature = false;
        ReadOnlyMemory<byte>? signature = null;
        string? password = null;

        switch (methodName)
        {
            case "none":
                break;

            case "publickey":
                hasSignature = reader.ReadBoolean();
                publicKeyAlgorithm = reader.ReadString();
                publicKeyBlob = reader.ReadBinaryString().ToArray();

                if (hasSignature)
                {
                    signature = reader.ReadBinaryString().ToArray();
                }
                break;

            case "password":
                var passwordChange = reader.ReadBoolean();
                if (passwordChange)
                {
                    throw new SshProtocolException("Password change not supported");
                }
                password = reader.ReadString();
                break;
        }

        return new UserauthRequestMessage
        {
            Username = username,
            ServiceName = serviceName,
            MethodName = methodName,
            PublicKeyAlgorithm = publicKeyAlgorithm,
            PublicKeyBlob = publicKeyBlob,
            HasSignature = hasSignature,
            Signature = signature,
            Password = password
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(Username);
        writer.WriteString(ServiceName);
        writer.WriteString(MethodName);

        switch (MethodName)
        {
            case "publickey":
                writer.WriteBoolean(HasSignature);
                writer.WriteString(PublicKeyAlgorithm!);
                writer.WriteBinaryString(PublicKeyBlob!.Value.Span);
                if (HasSignature)
                {
                    writer.WriteBinaryString(Signature!.Value.Span);
                }
                break;

            case "password":
                writer.WriteBoolean(false); // no password change
                writer.WriteString(Password!);
                break;

            case "none":
                // No additional data
                break;
        }

        return writer.Position;
    }

    public int GetSize()
    {
        var size = SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Username)) +
                   SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(ServiceName)) +
                   SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(MethodName));

        switch (MethodName)
        {
            case "publickey":
                size += 1; // hasSignature
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(PublicKeyAlgorithm!));
                size += SshWriter.GetBinaryStringSize(PublicKeyBlob!.Value.Length);
                if (HasSignature)
                {
                    size += SshWriter.GetBinaryStringSize(Signature!.Value.Length);
                }
                break;

            case "password":
                size += 1; // password change flag
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Password!));
                break;
        }

        return size;
    }
}

/// <summary>
/// SSH_MSG_USERAUTH_FAILURE (RFC 4252 §5.1)
/// </summary>
public sealed class UserauthFailureMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.UserauthFailure;

    /// <summary>
    /// Authentication methods that can continue.
    /// </summary>
    public required IReadOnlyList<string> AuthenticationsThatCanContinue { get; init; }

    /// <summary>
    /// Whether partial success was achieved.
    /// </summary>
    public bool PartialSuccess { get; init; }

    public static UserauthFailureMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new UserauthFailureMessage
        {
            AuthenticationsThatCanContinue = reader.ReadNameList(),
            PartialSuccess = reader.ReadBoolean()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteNameList(AuthenticationsThatCanContinue);
        writer.WriteBoolean(PartialSuccess);
        return writer.Position;
    }

    public int GetSize() =>
        SshWriter.GetNameListSize(AuthenticationsThatCanContinue) + 1;
}

/// <summary>
/// SSH_MSG_USERAUTH_SUCCESS (RFC 4252 §5.1)
/// </summary>
public sealed class UserauthSuccessMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.UserauthSuccess;

    public static UserauthSuccessMessage Instance { get; } = new();

    public static UserauthSuccessMessage Parse(ReadOnlySpan<byte> payload) => Instance;

    public int WriteTo(Span<byte> buffer) => 0;

    public int GetSize() => 0;
}

/// <summary>
/// SSH_MSG_USERAUTH_BANNER (RFC 4252 §5.4)
/// </summary>
public sealed class UserauthBannerMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.UserauthBanner;

    public required string Message { get; init; }
    public string LanguageTag { get; init; } = "";

    public static UserauthBannerMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new UserauthBannerMessage
        {
            Message = reader.ReadString(),
            LanguageTag = reader.ReadString()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(Message);
        writer.WriteString(LanguageTag);
        return writer.Position;
    }

    public int GetSize() =>
        SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Message)) +
        SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(LanguageTag));
}

/// <summary>
/// SSH_MSG_USERAUTH_PK_OK (RFC 4252 §7)
/// </summary>
public sealed class UserauthPkOkMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.UserauthPkOk;

    public required string Algorithm { get; init; }
    public required ReadOnlyMemory<byte> PublicKeyBlob { get; init; }

    public static UserauthPkOkMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new UserauthPkOkMessage
        {
            Algorithm = reader.ReadString(),
            PublicKeyBlob = reader.ReadBinaryString().ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(Algorithm);
        writer.WriteBinaryString(PublicKeyBlob.Span);
        return writer.Position;
    }

    public int GetSize() =>
        SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(Algorithm)) +
        SshWriter.GetBinaryStringSize(PublicKeyBlob.Length);
}
