namespace FeatherQuilld.Plugins.Events;

/// <summary>
/// Before → action → After helpers. Before Cancel aborts; Replace short-circuits the action.
/// After is observe-only (Cancel/Replace ignored for control flow).
/// </summary>
public static class EventBusExtensions
{
    public static IEventBus OrNoOp(this IEventBus? bus) => bus ?? NoOpEventBus.Instance;

    public static void WithHooks<TBefore, TAfter>(
        this IEventBus bus,
        TBefore before,
        Func<Exception?, TAfter> afterFactory,
        Action action)
        where TBefore : class
        where TAfter : class
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(afterFactory);
        ArgumentNullException.ThrowIfNull(action);

        ApplyBefore(bus, before);

        Exception? error = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            error = ex;
            EmitAfter(bus, afterFactory(ex));
            throw;
        }

        EmitAfter(bus, afterFactory(null));
    }

    public static T WithHooks<TBefore, TAfter, T>(
        this IEventBus bus,
        TBefore before,
        Func<T?, Exception?, TAfter> afterFactory,
        Func<T> action)
        where TBefore : class
        where TAfter : class
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(afterFactory);
        ArgumentNullException.ThrowIfNull(action);

        var beforeResult = bus.Emit(before);
        if (beforeResult.IsCancelled)
            throw new PluginHookCancelledException(typeof(TBefore).Name);
        if (beforeResult.IsReplaced)
        {
            var replaced = CastReplacement<T>(beforeResult.Replacement, typeof(TBefore).Name);
            EmitAfter(bus, afterFactory(replaced, null));
            return replaced;
        }

        T result;
        try
        {
            result = action();
        }
        catch (Exception ex)
        {
            EmitAfter(bus, afterFactory(default, ex));
            throw;
        }

        EmitAfter(bus, afterFactory(result, null));
        return result;
    }

    public static async Task WithHooksAsync<TBefore, TAfter>(
        this IEventBus bus,
        TBefore before,
        Func<Exception?, TAfter> afterFactory,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        where TBefore : class
        where TAfter : class
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(afterFactory);
        ArgumentNullException.ThrowIfNull(action);

        await ApplyBeforeAsync(bus, before, cancellationToken).ConfigureAwait(false);

        Exception? error = null;
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
            await EmitAfterAsync(bus, afterFactory(ex), cancellationToken).ConfigureAwait(false);
            throw;
        }

        await EmitAfterAsync(bus, afterFactory(null), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> WithHooksAsync<TBefore, TAfter, T>(
        this IEventBus bus,
        TBefore before,
        Func<T?, Exception?, TAfter> afterFactory,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
        where TBefore : class
        where TAfter : class
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(afterFactory);
        ArgumentNullException.ThrowIfNull(action);

        var beforeResult = await bus.EmitAsync(before, cancellationToken).ConfigureAwait(false);
        if (beforeResult.IsCancelled)
            throw new PluginHookCancelledException(typeof(TBefore).Name);
        if (beforeResult.IsReplaced)
        {
            var replaced = CastReplacement<T>(beforeResult.Replacement, typeof(TBefore).Name);
            await EmitAfterAsync(bus, afterFactory(replaced, null), cancellationToken).ConfigureAwait(false);
            return replaced;
        }

        T result;
        try
        {
            result = await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EmitAfterAsync(bus, afterFactory(default, ex), cancellationToken).ConfigureAwait(false);
            throw;
        }

        await EmitAfterAsync(bus, afterFactory(result, null), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static void ApplyBefore<TBefore>(IEventBus bus, TBefore before)
        where TBefore : class
    {
        var result = bus.Emit(before);
        if (result.IsCancelled)
            throw new PluginHookCancelledException(typeof(TBefore).Name);
        if (result.IsReplaced)
            throw new PluginHookCancelledException(
                typeof(TBefore).Name,
                $"Plugin hook Replace is not supported for void action {typeof(TBefore).Name}.");
    }

    private static async Task ApplyBeforeAsync<TBefore>(
        IEventBus bus,
        TBefore before,
        CancellationToken cancellationToken)
        where TBefore : class
    {
        var result = await bus.EmitAsync(before, cancellationToken).ConfigureAwait(false);
        if (result.IsCancelled)
            throw new PluginHookCancelledException(typeof(TBefore).Name);
        if (result.IsReplaced)
            throw new PluginHookCancelledException(
                typeof(TBefore).Name,
                $"Plugin hook Replace is not supported for void action {typeof(TBefore).Name}.");
    }

    private static void EmitAfter<TAfter>(IEventBus bus, TAfter after)
        where TAfter : class
    {
        // Observe-only: ignore Cancel/Replace.
        _ = bus.Emit(after);
    }

    private static async Task EmitAfterAsync<TAfter>(
        IEventBus bus,
        TAfter after,
        CancellationToken cancellationToken)
        where TAfter : class
    {
        _ = await bus.EmitAsync(after, cancellationToken).ConfigureAwait(false);
    }

    private static T CastReplacement<T>(object? replacement, string eventName)
    {
        if (replacement is T typed)
            return typed;
        throw new PluginHookCancelledException(
            eventName,
            $"Plugin hook Replace for {eventName} returned incompatible type "
            + $"{replacement?.GetType().Name ?? "null"}; expected {typeof(T).Name}.");
    }
}
