using Npgsql;
using RabbitMQ.Client;
using Sol.Infrastructure.Messaging;
using StackExchange.Redis;

namespace Sol.Infrastructure.Health;

/// <summary>Result of probing one dependency.</summary>
public sealed record DependencyHealth(string Name, bool Healthy, string? Error, double ElapsedMs);

/// <summary>
/// Probes the backing services.
/// </summary>
/// <remarks>
/// Hand-written rather than using the community <c>AspNetCore.HealthChecks.*</c> packages: their
/// AOT compatibility is unverified and each pulls in transitive dependencies for what amounts to
/// three one-line queries.
/// </remarks>
public sealed class DependencyHealthProbe(
    NpgsqlDataSource dataSource,
    IConnectionMultiplexer redis,
    RabbitMqConnectionProvider rabbit)
{
    public async Task<IReadOnlyList<DependencyHealth>> ProbeAllAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(
            ProbeAsync("postgres", async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(token);
            }, ct),
            ProbeAsync("redis", async _ =>
            {
                // PING only. StackExchange.Redis 3.x enforces AllowAdmin on Execute, and an
                // admin-flagged health check would need elevated permissions for no benefit.
                await redis.GetDatabase().PingAsync();
            }, ct),
            ProbeAsync("rabbitmq", async token =>
            {
                var connection = await rabbit.GetConnectionAsync(token);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
            }, ct));

        return results;
    }

    private static async Task<DependencyHealth> ProbeAsync(
        string name, Func<CancellationToken, Task> probe, CancellationToken ct)
    {
        var start = TimeProvider.System.GetTimestamp();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await probe(timeout.Token);
            return new DependencyHealth(name, true, null, Elapsed(start));
        }
        catch (Exception ex)
        {
            return new DependencyHealth(name, false, ex.Message, Elapsed(start));
        }
    }

    private static double Elapsed(long start) =>
        TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;
}
