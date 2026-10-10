namespace d0x2a.EmbeddedSsh.Auth;

/// <summary>
/// Simple password authenticator using a callback function.
/// </summary>
public sealed class PasswordAuthenticator : IAuthenticator
{
    private readonly Func<string, string, ValueTask<bool>> _validatePassword;

    /// <summary>
    /// Creates a password authenticator with a validation function.
    /// </summary>
    /// <param name="validatePassword">Function that validates (username, password) and returns true if valid.</param>
    public PasswordAuthenticator(Func<string, string, ValueTask<bool>> validatePassword)
    {
        _validatePassword = validatePassword ?? throw new ArgumentNullException(nameof(validatePassword));
    }

    /// <summary>
    /// Creates a password authenticator with a dictionary of username/password pairs.
    /// </summary>
    public PasswordAuthenticator(IDictionary<string, string> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _validatePassword = (username, password) =>
        {
            if (credentials.TryGetValue(username, out var storedPassword))
            {
                // Use constant-time comparison to prevent timing attacks
                return ValueTask.FromResult(
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(password),
                        System.Text.Encoding.UTF8.GetBytes(storedPassword)));
            }
            return ValueTask.FromResult(false);
        };
    }

    public IEnumerable<string> SupportedMethods => ["password"];

    public async ValueTask<(AuthResult Result, AuthenticatedUser? User)> AuthenticateAsync(
        AuthContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Method != "password")
            return (AuthResult.Failure, null);

        if (string.IsNullOrEmpty(context.Password))
            return (AuthResult.Failure, null);

        var valid = await _validatePassword(context.Username, context.Password).ConfigureAwait(false);

        if (!valid)
            return (AuthResult.Failure, null);

        var user = new AuthenticatedUser
        {
            Username = context.Username,
            Method = "password"
        };

        return (AuthResult.Success, user);
    }

    public ValueTask<bool> IsPublicKeyAcceptableAsync(
        string username,
        string algorithm,
        ReadOnlyMemory<byte> publicKeyBlob,
        CancellationToken cancellationToken = default)
    {
        // Password authenticator doesn't support public keys
        return ValueTask.FromResult(false);
    }
}

/// <summary>
/// Composite authenticator that chains multiple authenticators.
/// </summary>
public sealed class CompositeAuthenticator : IAuthenticator
{
    private readonly IAuthenticator[] _authenticators;

    public CompositeAuthenticator(params IAuthenticator[] authenticators)
    {
        _authenticators = authenticators ?? throw new ArgumentNullException(nameof(authenticators));
    }

    public IEnumerable<string> SupportedMethods =>
        _authenticators.SelectMany(a => a.SupportedMethods).Distinct();

    public async ValueTask<(AuthResult Result, AuthenticatedUser? User)> AuthenticateAsync(
        AuthContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var auth in _authenticators)
        {
            if (auth.SupportedMethods.Contains(context.Method))
            {
                var (result, user) = await auth.AuthenticateAsync(context, cancellationToken)
                    .ConfigureAwait(false);

                if (result == AuthResult.Success)
                    return (result, user);
            }
        }

        return (AuthResult.Failure, null);
    }

    public async ValueTask<bool> IsPublicKeyAcceptableAsync(
        string username,
        string algorithm,
        ReadOnlyMemory<byte> publicKeyBlob,
        CancellationToken cancellationToken = default)
    {
        foreach (var auth in _authenticators)
        {
            if (await auth.IsPublicKeyAcceptableAsync(username, algorithm, publicKeyBlob, cancellationToken)
                .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }
}
