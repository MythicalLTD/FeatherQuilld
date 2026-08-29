using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Events;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.Plugins;

public class DomainHookCancelTests
{
    [Fact]
    public void FileWrite_Cancel_DoesNotWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var uuid = Guid.NewGuid();
            var bus = new EventBus();
            bus.On<FileWriteBeforeEvent>(_ => HookResult.Cancel());
            var files = new WebSpaceFileService(new FakeFs(uuid, root), events: bus);

            Assert.Throws<PluginHookCancelledException>(() =>
                files.WriteText(uuid, "/blocked.txt", "nope"));

            Assert.False(File.Exists(Path.Combine(root, "blocked.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void AccessDeauthorize_Cancel_LeavesState()
    {
        var bus = new EventBus();
        bus.On<AccessDeauthorizeBeforeEvent>(_ => HookResult.Cancel());
        var access = new WebSpaceUserAccessService(bus);
        var user = Guid.NewGuid();
        var ws = Guid.NewGuid();

        Assert.Throws<PluginHookCancelledException>(() =>
            access.Deauthorize(user, [ws]));

        Assert.False(access.IsJwtRevoked(user, ws, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    private sealed class FakeFs(Guid uuid, string root) : IWebSpaceFsAccess
    {
        public WebSpace? Get(Guid id) =>
            id == uuid
                ? new WebSpace { Uuid = id, Name = "test", Status = WebSpaceStatus.Installed }
                : null;

        public string EffectiveFsPath(Guid id) =>
            id == uuid ? root : throw new InvalidOperationException("missing");
    }
}
