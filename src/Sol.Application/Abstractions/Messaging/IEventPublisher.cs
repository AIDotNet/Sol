using System.Text.Json.Serialization.Metadata;

namespace Sol.Application.Abstractions.Messaging;

/// <summary>
/// Publishes integration events to the message broker.
/// </summary>
/// <remarks>
/// As with <see cref="Caching.ICacheStore"/>, the caller supplies source-generated JSON
/// metadata so no reflective serialization path exists to fall into under AOT.
/// </remarks>
public interface IEventPublisher
{
    Task PublishAsync<T>(string routingKey, T payload, JsonTypeInfo<T> typeInfo, CancellationToken ct);
}
