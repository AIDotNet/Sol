namespace Sol.Infrastructure.Messaging;

/// <summary>
/// Maps routing keys to handlers.
/// </summary>
/// <remarks>
/// Populated by hand in <c>DependencyInjection</c>. Assembly scanning is the usual approach and
/// is unusable here: under Native AOT it finds nothing, registers nothing, and reports no error,
/// so consumers would silently process no messages in production while every JIT-mode test passes.
/// </remarks>
public sealed class IntegrationEventHandlerRegistry
{
    private readonly Dictionary<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task>> _handlers = new(StringComparer.Ordinal);

    public IntegrationEventHandlerRegistry Register(
        string routingKey, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        _handlers[routingKey] = handler;
        return this;
    }

    public bool CanHandle(string routingKey) => _handlers.ContainsKey(routingKey);

    /// <summary>Returns false when no handler is registered, so the caller can ack and move on.</summary>
    public async Task<bool> TryDispatchAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(routingKey, out var handler))
        {
            return false;
        }

        await handler(body, ct);
        return true;
    }
}
