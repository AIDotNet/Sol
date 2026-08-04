using RabbitMQ.Client;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Messaging;

/// <summary>
/// Declares exchanges and queues. Declarations are idempotent, so this runs on every startup
/// and on every consumer reconnect.
/// </summary>
public static class RabbitMqTopology
{
    public static string DeadLetterExchange(RabbitMqOptions options) => options.Exchange + ".dlx";

    public static string DeadLetterQueue(RabbitMqOptions options) => options.QueueName + ".dlq";

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(
            exchange: options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        // A message that keeps failing must end up somewhere inspectable rather than looping
        // forever or vanishing.
        var dlx = DeadLetterExchange(options);
        var dlq = DeadLetterQueue(options);

        await channel.ExchangeDeclareAsync(
            exchange: dlx, type: ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: dlq, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        await channel.QueueBindAsync(queue: dlq, exchange: dlx, routingKey: string.Empty, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = dlx },
            cancellationToken: ct);

        foreach (var routingKey in options.RoutingKeys)
        {
            await channel.QueueBindAsync(
                queue: options.QueueName,
                exchange: options.Exchange,
                routingKey: routingKey,
                cancellationToken: ct);
        }
    }
}
