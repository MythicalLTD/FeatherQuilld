namespace FeatherQuilld.Utils.Auth;

/// <summary>Permission strings carried in console JWTs.</summary>
public static class ConsolePermissions
{
    public const string Wildcard = "*";
    public const string Output = "console.output";
    public const string Send = "console.send";

    public static bool Allows(IReadOnlyList<string> granted, string required)
    {
        foreach (var p in granted)
        {
            if (string.Equals(p, Wildcard, StringComparison.Ordinal)
                || string.Equals(p, required, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
