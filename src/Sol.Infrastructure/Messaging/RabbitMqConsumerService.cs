using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Messaging;

/// <summary>
/// Consumes integration events, reconnecting with backoff when the broker or channel drops.
/// </summary>
public sealed class RabbitMqConsumerService(
    RabbitMqConnectionProvider connections,
    IntegrationEventHandlerRegistry registry,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RabbitMQ consumer loop failed; retrying in {Backoff}.", backoff);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var connection = await connections.GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await RabbitMqTopology.DeclareAsync(channel, _options, ct);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, ct);

        await channel.BasicConsumeAsync(
            queue: _options.QueueName, autoAck: false, consumer: consumer, cancellationToken: ct);

        logger.LogInformation(
            "Consuming {Queue} bound to {Exchange} on [{RoutingKeys}].",
            _options.QueueName, _options.Exchange, string.Join(", ", _options.RoutingKeys));

        // Hold the channel open until shutdown; delivery happens on the consumer callback.
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        // The library reuses the buffer behind ea.Body the moment this handler yields, so the
        // payload must be copied before any await. Holding the ReadOnlyMemory produces corrupt
        // data under load and is essentially impossible to reproduce on demand.
        var body = ea.Body.ToArray();

        try
        {
            var handled = await registry.TryDispatchAsync(ea.RoutingKey, body, ct);
            if (!handled)
            {
                logger.LogWarning("No handler for routing key {RoutingKey}; discarding.", ea.RoutingKey);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            // Retry once, then let the dead-letter exchange take it. Unbounded requeue turns one
            // poison message into an infinite loop that starves every other message in the queue.
            var requeue = !ea.Redelivered;
            logger.LogError(
                ex, "Handler for {RoutingKey} failed; requeue={Requeue}.", ea.RoutingKey, requeue);

            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: requeue, ct);
        }
    }
}
