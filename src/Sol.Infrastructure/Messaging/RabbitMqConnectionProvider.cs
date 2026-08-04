using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Messaging;

/// <summary>
/// Owns the single long-lived AMQP connection.
/// </summary>
/// <remarks>
/// A connection is expensive and thread-safe, so it is shared; channels are cheap and are
/// <em>not</em> thread-safe, so each publisher and consumer creates its own. Connections are
/// opened lazily under a semaphore rather than in the constructor, so a broker that is briefly
/// unavailable delays first use instead of preventing startup.
/// </remarks>
public sealed class RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;

    public async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
            };

            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
    }
}
