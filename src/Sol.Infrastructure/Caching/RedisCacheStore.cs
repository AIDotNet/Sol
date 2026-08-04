using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Caching;
using Sol.Infrastructure.Options;
using StackExchange.Redis;

namespace Sol.Infrastructure.Caching;

/// <remarks>
/// Deliberately avoids <c>LuaScript</c> and anything routed through
/// <c>ScriptParameterMapper</c>: those build a delegate with <c>Expression.Compile()</c>, which
/// throws <see cref="PlatformNotSupportedException"/> under Native AOT. Where a script is
/// genuinely needed, use the raw
/// <c>ScriptEvaluateAsync(string, RedisKey[], RedisValue[])</c> overload instead — see
/// <see cref="RedisDistributedLock"/>.
/// </remarks>
public sealed class RedisCacheStore(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
    : ICacheStore
{
    private readonly string _prefix = options.Value.InstanceName;

    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var value = await multiplexer.GetDatabase().StringGetAsync(Key(key));
        if (value.IsNullOrEmpty)
        {
            return default;
        }

        return JsonSerializer.Deserialize((string)value!, typeInfo);
    }

    public async Task SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo, TimeSpan ttl, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        await multiplexer.GetDatabase().StringSetAsync(Key(key), json, ttl);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct) =>
        await multiplexer.GetDatabase().KeyDeleteAsync(Key(key));

    private string Key(string key) => _prefix + key;
}
