using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Events;

namespace FeatherQuilld.Tests.Plugins;

public class EventBusExtensionsTests
{
    [Fact]
    public void WithHooks_Cancel_Throws()
    {
        var bus = new EventBus();
        bus.On<WebSpacePowerBeforeEvent>(_ => HookResult.Cancel());

        Assert.Throws<PluginHookCancelledException>(() =>
            bus.WithHooks(
                new WebSpacePowerBeforeEvent { WebSpaceUuid = Guid.NewGuid(), Action = "start" },
                err => new WebSpacePowerAfterEvent
                {
                    WebSpaceUuid = Guid.Empty,
                    Action = "start",
                    Error = err,
                },
                () => { }));
    }

    [Fact]
    public void WithHooks_Replace_SkipsAction()
    {
        var bus = new EventBus();
        bus.On<FileReadBeforeEvent>(_ => HookResult.Replace("from-plugin"));

        var ran = false;
        var result = bus.WithHooks(
            new FileReadBeforeEvent { WebSpaceUuid = Guid.NewGuid(), Path = "/a" },
            (contents, err) => new FileReadAfterEvent
            {
                WebSpaceUuid = Guid.Empty,
                Path = "/a",
                Contents = contents,
                Error = err,
            },
            () =>
            {
                ran = true;
                return "from-disk";
            });

        Assert.False(ran);
        Assert.Equal("from-plugin", result);
    }

    [Fact]
    public void WithHooks_AfterRunsOnSuccess()
    {
        var bus = new EventBus();
        var afterSeen = false;
        bus.On<WebSpacePowerAfterEvent>(_ =>
        {
            afterSeen = true;
            return HookResult.Cancel(); // observe-only
        });

        bus.WithHooks(
            new WebSpacePowerBeforeEvent { WebSpaceUuid = Guid.NewGuid(), Action = "stop" },
            err => new WebSpacePowerAfterEvent
            {
                WebSpaceUuid = Guid.Empty,
                Action = "stop",
                Error = err,
            },
            () => { });

        Assert.True(afterSeen);
    }
}
