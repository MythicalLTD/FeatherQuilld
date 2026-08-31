using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceTrashServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Guid _uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly WebSpaceTrashService _trash;
    private readonly WebSpaceFileService _files;

    public WebSpaceTrashServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-trash-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "public"));
        File.WriteAllText(Path.Combine(_root, "webspace.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "public", "note.txt"), "hello");

        var access = new FakeFsAccess(_uuid, _root);
        _trash = new WebSpaceTrashService(access);
        _files = new WebSpaceFileService(access);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void MoveToTrash_ThenList_AndRestore()
    {
        _files.Delete(_uuid, ["/public/note.txt"], useTrash: true);
        Assert.False(File.Exists(Path.Combine(_root, "public", "note.txt")));

        var listed = _trash.ListTrash(_uuid, 1024 * 1024, 30);
        Assert.Single(listed.Entries);
        Assert.Equal("note.txt", listed.Entries[0].OriginalName);
        Assert.Equal("/public", listed.Entries[0].OriginalRoot);

        _trash.RestoreTrash(_uuid, [listed.Entries[0].Id], overwrite: false);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "public", "note.txt")));
    }

    [Fact]
    public void EmptyTrash_RemovesAllEntries()
    {
        _files.Delete(_uuid, ["/public/note.txt"], useTrash: true);
        _trash.EmptyTrash(_uuid);
        var listed = _trash.ListTrash(_uuid, 1024 * 1024, 30);
        Assert.Empty(listed.Entries);
    }

    [Fact]
    public void RestoreTrash_WithoutOverwrite_ThrowsWhenTargetExists()
    {
        _files.Delete(_uuid, ["/public/note.txt"], useTrash: true);
        File.WriteAllText(Path.Combine(_root, "public", "note.txt"), "replacement");

        var listed = _trash.ListTrash(_uuid, 1024 * 1024, 30);
        Assert.Throws<InvalidOperationException>(() =>
            _trash.RestoreTrash(_uuid, [listed.Entries[0].Id], overwrite: false));
    }

    [Fact]
    public void ListTrash_PurgesExpiredEntries()
    {
        _files.Delete(_uuid, ["/public/note.txt"], useTrash: true);
        var trashRoot = Path.Combine(_root, WebSpaceTrashService.TrashDirName);
        var entryDir = Directory.EnumerateDirectories(trashRoot).Single();
        var metaPath = Path.Combine(entryDir, "meta.json");
        var json = File.ReadAllText(metaPath);
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"deleted_at\"\\s*:\\s*\"[^\"]+\"",
            $"\"deleted_at\":\"{DateTime.UtcNow.AddDays(-60):O}\"");
        File.WriteAllText(metaPath, json);

        var listed = _trash.ListTrash(_uuid, 1024 * 1024, 30);
        Assert.Empty(listed.Entries);
        Assert.False(Directory.Exists(entryDir));
    }

    private sealed class FakeFsAccess : IWebSpaceFsAccess
    {
        private readonly Guid _uuid;
        private readonly string _root;

        public FakeFsAccess(Guid uuid, string root)
        {
            _uuid = uuid;
            _root = root;
        }

        public WebSpace? Get(Guid uuid) =>
            uuid == _uuid
                ? new WebSpace { Uuid = uuid, Name = "test", Status = WebSpaceStatus.Installed }
                : null;

        public string EffectiveFsPath(Guid uuid) => _root;
    }
}
