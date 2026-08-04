using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Sol.Application.Abstractions.Messaging;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Messaging;

/// <summary>
/// Publishes integration events to the topic exchange with publisher confirms enabled.
/// </summary>
public sealed class RabbitMqEventPublisher(
    RabbitMqConnectionProvider connections,
    IOptions<RabbitMqOptions> options) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RabbitMqOptions _options = options.Value;
    private IChannel? _channel;

    public async Task PublishAsync<T>(string routingKey, T payload, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var channel = await GetChannelAsync(ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = Guid.CreateVersion7().ToString("N"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: ct);
    }

    /// <summary>
    /// Channels are not thread-safe, so the single cached channel is guarded. Publishing is
    /// serialised as a result — acceptable at this volume, and the correct default.
    /// </summary>
    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true } current)
            {
                return current;
            }

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            var connection = await connections.GetConnectionAsync(ct);
            _channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                ct);

            await RabbitMqTopology.DeclareAsync(_channel, _options, ct);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        _gate.Dispose();
    }
}
