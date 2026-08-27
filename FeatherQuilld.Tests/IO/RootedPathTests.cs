using FeatherQuilld.Utils.IO;

namespace FeatherQuilld.Tests.IO;

public class RootedPathTests
{
    [Fact]
    public void Resolve_RootAndNested_Succeed()
    {
        var root = RootedPath.CanonicalizeRoot(Path.Combine(Path.GetTempPath(), "fq-rooted-" + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(root, RootedPath.Resolve(root, "/"));
            Assert.Equal(root, RootedPath.Resolve(root, "."));
            var nested = RootedPath.Resolve(root, "public/index.html", allowMissing: true);
            Assert.StartsWith(root + Path.DirectorySeparatorChar, nested, StringComparison.Ordinal);
            Assert.EndsWith(Path.Combine("public", "index.html"), nested);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ParentEscape_Throws()
    {
        var root = RootedPath.CanonicalizeRoot(Path.Combine(Path.GetTempPath(), "fq-rooted-" + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => RootedPath.Resolve(root, "../escape"));
            Assert.Throws<UnauthorizedAccessException>(() => RootedPath.Resolve(root, "a/../../escape"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_LeadingSlash_IsRelativeToJailRoot()
    {
        var root = RootedPath.CanonicalizeRoot(Path.Combine(Path.GetTempPath(), "fq-rooted-" + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        try
        {
            // Virtual absolute paths are jailed (not host absolute).
            var path = RootedPath.Resolve(root, "/etc/passwd", allowMissing: true);
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            Assert.EndsWith(Path.Combine("etc", "passwd"), path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_WritableMissingFileUnderRoot_Ok()
    {
        var root = RootedPath.CanonicalizeRoot(Path.Combine(Path.GetTempPath(), "fq-rooted-" + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        try
        {
            var path = RootedPath.Resolve(root, "public/new.txt", allowMissing: true);
            Assert.False(File.Exists(path));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_SymlinkOutOfJail_Throws()
    {
        var root = RootedPath.CanonicalizeRoot(Path.Combine(Path.GetTempPath(), "fq-rooted-" + Guid.NewGuid()));
        var outside = Path.Combine(Path.GetTempPath(), "fq-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "nope");
            var link = Path.Combine(root, "escape");
            File.CreateSymbolicLink(link, outside);

            Assert.Throws<UnauthorizedAccessException>(() =>
                RootedPath.Resolve(root, "escape/secret.txt", allowMissing: true, followExistingLinks: true));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
            try { Directory.Delete(outside, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ToVirtual_And_IsUnderRoot_RoundTrip()
    {
        var root = RootedPath.CanonicalizeRoot(Path.Combine(Path.GetTempPath(), "fq-rooted-" + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        try
        {
            var nested = Path.Combine(root, "a", "b.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "x");
            Assert.True(RootedPath.IsUnderRoot(root, nested));
            Assert.Equal("/a/b.txt", RootedPath.ToVirtual(root, nested));
            Assert.Equal("/", RootedPath.ToVirtual(root, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
