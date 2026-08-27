using FeatherQuilld.Plugins.Events;

namespace FeatherQuilld.Tests.Plugins;

public class HookResultTests
{
    [Fact]
    public void Continue_IsDefaultContinue()
    {
        var r = HookResult.Continue();
        Assert.Equal(HookAction.Continue, r.Action);
        Assert.False(r.IsCancelled);
        Assert.False(r.IsReplaced);
        Assert.Null(r.Replacement);
    }

    [Fact]
    public void Cancel_SetsCancelFlag()
    {
        var r = HookResult.Cancel();
        Assert.True(r.IsCancelled);
        Assert.Equal(HookAction.Cancel, r.Action);
    }

    [Fact]
    public void Replace_CarriesValue()
    {
        var r = HookResult.Replace(42);
        Assert.True(r.IsReplaced);
        Assert.Equal(42, r.Replacement);
    }
}
