namespace FeatherQuilld.Utils.IO;

/// <summary>
/// Path confinement under a filesystem root (WebSpace files / SFTP parity).
/// </summary>
public static class RootedPath
{
    public static string CanonicalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var full = Path.GetFullPath(rootPath.Trim());
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static bool IsUnderRoot(string root, string path)
    {
        var canonical = CanonicalizeRoot(root);
        if (string.Equals(path, canonical, StringComparison.Ordinal))
            return true;
        var rootPrefix = canonical.EndsWith(Path.DirectorySeparatorChar)
            ? canonical
            : canonical + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, StringComparison.Ordinal);
    }

    public static string ToVirtual(string root, string fullPath)
    {
        var canonical = CanonicalizeRoot(root);
        if (string.Equals(fullPath, canonical, StringComparison.Ordinal))
            return "/";
        var rootPrefix = canonical.EndsWith(Path.DirectorySeparatorChar)
            ? canonical
            : canonical + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            return "/";
        var rel = fullPath[rootPrefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
        return "/" + rel;
    }

    /// <summary>
    /// Resolve through existing symlink / parent components when present.
    /// </summary>
    public static string ResolveExisting(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                var target = di.ResolveLinkTarget(returnFinalTarget: true);
                return Path.GetFullPath(target?.FullName ?? di.FullName);
            }

            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                    return Path.GetFullPath(target.FullName);
                var parent = fi.Directory?.FullName;
                if (parent != null)
                {
                    var resolvedParent = ResolveExisting(parent);
                    return Path.GetFullPath(Path.Combine(resolvedParent, fi.Name));
                }

                return Path.GetFullPath(fi.FullName);
            }

            var cur = path;
            var parts = new Stack<string>();
            while (!string.IsNullOrEmpty(cur) && !Directory.Exists(cur) && !File.Exists(cur))
            {
                var name = Path.GetFileName(cur);
                var parent = Path.GetDirectoryName(cur);
                if (parent is null || parent == cur)
                    break;
                parts.Push(name);
                cur = parent;
            }

            var basePath = Directory.Exists(cur) || File.Exists(cur)
                ? ResolveExisting(cur)
                : Path.GetFullPath(cur);
            while (parts.Count > 0)
                basePath = Path.GetFullPath(Path.Combine(basePath, parts.Pop()));
            return basePath;
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Resolve a virtual path under <paramref name="root"/>.
    /// Throws <see cref="UnauthorizedAccessException"/> if the result escapes the root.
    /// </summary>
    /// <param name="followExistingLinks">
    /// When true, resolve existing symlink leaves / parents (SFTP). When false, logical
    /// <see cref="Path.GetFullPath"/> only (HTTP file API).
    /// </param>
    public static string Resolve(
        string root,
        string? virtualPath,
        bool allowMissing = false,
        bool followExistingLinks = false)
    {
        var canonical = CanonicalizeRoot(root);
        var rootPrefix = canonical.EndsWith(Path.DirectorySeparatorChar)
            ? canonical
            : canonical + Path.DirectorySeparatorChar;

        var rel = (virtualPath ?? "/").Replace('\\', '/').Trim();
        if (rel.StartsWith('/'))
            rel = rel[1..];
        if (rel is "" or ".")
            return followExistingLinks ? ResolveExisting(canonical) : canonical;

        var combined = Path.GetFullPath(Path.Combine(canonical, rel.Replace('/', Path.DirectorySeparatorChar)));
        var resolved = followExistingLinks ? ResolveExisting(combined) : combined;

        if (!string.Equals(resolved, canonical, StringComparison.Ordinal)
            && !resolved.StartsWith(rootPrefix, StringComparison.Ordinal)
            && !IsUnderRoot(canonical, resolved))
            throw new UnauthorizedAccessException("Path escapes WebSpace root.");

        if (followExistingLinks
            && allowMissing
            && !File.Exists(resolved)
            && !Directory.Exists(resolved))
        {
            var parent = Path.GetDirectoryName(resolved)
                         ?? throw new UnauthorizedAccessException("Path escapes WebSpace root.");
            var resolvedParent = ResolveExisting(parent);
            if (!IsUnderRoot(canonical, resolvedParent))
                throw new UnauthorizedAccessException("Path escapes WebSpace root.");
        }

        _ = allowMissing; // callers may still check existence separately when not following links
        return resolved;
    }
}
