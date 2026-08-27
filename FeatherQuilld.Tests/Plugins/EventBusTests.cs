using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Events;

namespace FeatherQuilld.Tests.Plugins;

public class EventBusTests
{
    private sealed class SampleEvent
    {
        public int Value { get; set; }
    }

    [Fact]
    public void Emit_PriorityAscending_FirstCancelsStopsRest()
    {
        var bus = new EventBus();
        var order = new List<int>();

        bus.On<SampleEvent>(_ => { order.Add(1); return HookResult.Cancel(); }, priority: 1);
        bus.On<SampleEvent>(_ => { order.Add(2); return HookResult.Continue(); }, priority: 2);

        var result = bus.Emit(new SampleEvent());
        Assert.True(result.IsCancelled);
        Assert.Equal(new[] { 1 }, order);
    }

    [Fact]
    public void Emit_LowerPriorityRunsBeforeHigher()
    {
        var bus = new EventBus();
        var order = new List<int>();
        bus.On<SampleEvent>(_ => { order.Add(20); return HookResult.Continue(); }, priority: 20);
        bus.On<SampleEvent>(_ => { order.Add(5); return HookResult.Continue(); }, priority: 5);
        bus.Emit(new SampleEvent());
        Assert.Equal(new[] { 5, 20 }, order);
    }

    [Fact]
    public void Emit_Replace_ReturnsReplacement()
    {
        var bus = new EventBus();
        bus.On<SampleEvent>(_ => HookResult.Replace("x"));
        var result = bus.Emit(new SampleEvent());
        Assert.True(result.IsReplaced);
        Assert.Equal("x", result.Replacement);
    }

    [Fact]
    public void Dispose_UnsubscribesHandler()
    {
        var bus = new EventBus();
        var calls = 0;
        var sub = bus.On<SampleEvent>(_ =>
        {
            calls++;
            return HookResult.Continue();
        });
        sub.Dispose();
        bus.Emit(new SampleEvent());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Emit_NoHandlers_Continues()
    {
        var bus = new EventBus();
        Assert.Equal(HookAction.Continue, bus.Emit(new SampleEvent()).Action);
    }
}
