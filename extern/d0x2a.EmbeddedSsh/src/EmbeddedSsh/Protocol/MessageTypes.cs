namespace d0x2a.EmbeddedSsh.Protocol;

/// <summary>
/// SSH message type numbers (RFC 4250).
/// </summary>
public enum SshMessageType : byte
{
    // Transport layer (1-49)

    /// <summary>Disconnect message (RFC 4253 §11.1)</summary>
    Disconnect = 1,

    /// <summary>Ignore message for keep-alive (RFC 4253 §11.2)</summary>
    Ignore = 2,

    /// <summary>Unimplemented message response (RFC 4253 §11.4)</summary>
    Unimplemented = 3,

    /// <summary>Debug message (RFC 4253 §11.3)</summary>
    Debug = 4,

    /// <summary>Service request (RFC 4253 §10)</summary>
    ServiceRequest = 5,

    /// <summary>Service accept (RFC 4253 §10)</summary>
    ServiceAccept = 6,

    /// <summary>Extension info (RFC 8308)</summary>
    ExtInfo = 7,

    /// <summary>Key exchange init (RFC 4253 §7.1)</summary>
    KexInit = 20,

    /// <summary>New keys (RFC 4253 §7.3)</summary>
    NewKeys = 21,

    // KEX method-specific (30-49)

    /// <summary>ECDH key exchange init (RFC 5656)</summary>
    KexEcdhInit = 30,

    /// <summary>ECDH key exchange reply (RFC 5656)</summary>
    KexEcdhReply = 31,

    // Authentication (50-79)

    /// <summary>User authentication request (RFC 4252 §5)</summary>
    UserauthRequest = 50,

    /// <summary>User authentication failure (RFC 4252 §5.1)</summary>
    UserauthFailure = 51,

    /// <summary>User authentication success (RFC 4252 §5.1)</summary>
    UserauthSuccess = 52,

    /// <summary>User authentication banner (RFC 4252 §5.4)</summary>
    UserauthBanner = 53,

    /// <summary>Public key OK response (RFC 4252 §7)</summary>
    UserauthPkOk = 60,

    // Connection (80-127)

    /// <summary>Global request (RFC 4254 §4)</summary>
    GlobalRequest = 80,

    /// <summary>Request success (RFC 4254 §4)</summary>
    RequestSuccess = 81,

    /// <summary>Request failure (RFC 4254 §4)</summary>
    RequestFailure = 82,

    /// <summary>Channel open (RFC 4254 §5.1)</summary>
    ChannelOpen = 90,

    /// <summary>Channel open confirmation (RFC 4254 §5.1)</summary>
    ChannelOpenConfirmation = 91,

    /// <summary>Channel open failure (RFC 4254 §5.1)</summary>
    ChannelOpenFailure = 92,

    /// <summary>Channel window adjust (RFC 4254 §5.2)</summary>
    ChannelWindowAdjust = 93,

    /// <summary>Channel data (RFC 4254 §5.2)</summary>
    ChannelData = 94,

    /// <summary>Channel extended data (RFC 4254 §5.2)</summary>
    ChannelExtendedData = 95,

    /// <summary>Channel EOF (RFC 4254 §5.3)</summary>
    ChannelEof = 96,

    /// <summary>Channel close (RFC 4254 §5.3)</summary>
    ChannelClose = 97,

    /// <summary>Channel request (RFC 4254 §5.4)</summary>
    ChannelRequest = 98,

    /// <summary>Channel success (RFC 4254 §5.4)</summary>
    ChannelSuccess = 99,

    /// <summary>Channel failure (RFC 4254 §5.4)</summary>
    ChannelFailure = 100,
}

/// <summary>
/// Channel open failure reason codes (RFC 4254 §5.1).
/// </summary>
public enum ChannelOpenFailureReason : uint
{
    AdministrativelyProhibited = 1,
    ConnectFailed = 2,
    UnknownChannelType = 3,
    ResourceShortage = 4,
}

/// <summary>
/// Extended data type codes (RFC 4254 §5.2).
/// </summary>
public enum ExtendedDataType : uint
{
    Stderr = 1,
}
