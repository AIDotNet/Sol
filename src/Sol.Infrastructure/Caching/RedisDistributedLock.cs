using Microsoft.Extensions.Options;
using Sol.Infrastructure.Options;
using StackExchange.Redis;

namespace Sol.Infrastructure.Caching;

/// <summary>
/// A best-effort mutual exclusion lock over Redis (SET NX PX).
/// </summary>
/// <remarks>
/// Suitable for suppressing duplicate work, not for guarding correctness: a process that pauses
/// past the TTL can lose the lock while still believing it holds it. Where correctness depends
/// on exclusion, use a database transaction or a PostgreSQL advisory lock instead.
/// </remarks>
public sealed class RedisDistributedLock(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
{
    /// <summary>
    /// Releases only if the token still matches, so a lock that already expired and was
    /// re-acquired elsewhere is not deleted by its previous owner.
    /// </summary>
    private const string ReleaseScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    private readonly string _prefix = options.Value.InstanceName + "lock:";

    public async Task<string?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken ct)
    {
        var token = Guid.CreateVersion7().ToString("N");
        var acquired = await multiplexer.GetDatabase()
            .StringSetAsync(_prefix + name, token, ttl, When.NotExists);

        return acquired ? token : null;
    }

    public async Task<bool> ReleaseAsync(string name, string token, CancellationToken ct)
    {
        // Raw script form with explicit key/value arrays. The LuaScript.Prepare(...) API builds
        // its parameter mapper with Expression.Compile() and therefore fails under Native AOT.
        var result = await multiplexer.GetDatabase().ScriptEvaluateAsync(
            ReleaseScript,
            [_prefix + name],
            [token]);

        return (long)result == 1;
    }
}
