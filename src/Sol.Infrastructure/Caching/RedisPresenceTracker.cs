using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Caching;
using Sol.Domain.Identity;
using Sol.Infrastructure.Options;
using StackExchange.Redis;

namespace Sol.Infrastructure.Caching;

/// <summary>
/// Tracks live realtime connections per device in a Redis set.
/// </summary>
/// <remarks>
/// A set rather than a counter, because a device may hold several connections (multiple tabs)
/// and a counter drifts permanently upward when a process dies without running its disconnect
/// handler. Set membership is idempotent, and the TTL bounds the damage from a lost cleanup.
/// </remarks>
public sealed class RedisPresenceTracker(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
    : IPresenceTracker
{
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromHours(12);

    private readonly string _prefix = options.Value.InstanceName + "presence:";

    public async Task MarkOnlineAsync(DeviceId deviceId, string connectionId, CancellationToken ct)
    {
        var db = multiplexer.GetDatabase();
        var key = Key(deviceId);
        await db.SetAddAsync(key, connectionId);
        await db.KeyExpireAsync(key, PresenceTtl);
    }

    public async Task MarkOfflineAsync(DeviceId deviceId, string connectionId, CancellationToken ct)
    {
        var db = multiplexer.GetDatabase();
        var key = Key(deviceId);
        await db.SetRemoveAsync(key, connectionId);

        // Drop the key entirely once the last connection goes, so idle devices do not
        // accumulate empty sets.
        if (await db.SetLengthAsync(key) == 0)
        {
            await db.KeyDeleteAsync(key);
        }
    }

    public async Task<bool> IsOnlineAsync(DeviceId deviceId, CancellationToken ct) =>
        await multiplexer.GetDatabase().SetLengthAsync(Key(deviceId)) > 0;

    public async Task<long> ConnectionCountAsync(DeviceId deviceId, CancellationToken ct) =>
        await multiplexer.GetDatabase().SetLengthAsync(Key(deviceId));

    private RedisKey Key(DeviceId deviceId) => _prefix + deviceId.Value.ToString("N");
}
