namespace FeatherQuilld.Utils.Config.Sentry;

/// <summary>
/// Error reporting via GlitchTip (Sentry-compatible). Enabled by default;
/// set <see cref="Enabled"/> to false or clear <see cref="Dsn"/> to disable.
/// </summary>
public class SentryConfig
{
    public const string DefaultDsn =
        "https://468820fa14494a48a8c3e518972572b0@error.mythical.systems/5";

    /// <summary>When true and <see cref="Dsn"/> is set, the SDK is initialized at daemon start.</summary>
    public bool Enabled { get; set; } = true;

    public string Dsn { get; set; } = DefaultDsn;

    /// <summary>Fraction of transactions to send (0–1). Keep low in production.</summary>
    public double TracesSampleRate { get; set; } = 0.01;

    public bool IsActive =>
        Enabled && !string.IsNullOrWhiteSpace(Dsn);
}
