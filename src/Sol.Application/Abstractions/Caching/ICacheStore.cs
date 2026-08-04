using System.Text.Json.Serialization.Metadata;

namespace Sol.Application.Abstractions.Caching;

/// <summary>
/// Key/value cache.
/// </summary>
/// <remarks>
/// <see cref="JsonTypeInfo{T}"/> is part of the signature on purpose. Under Native AOT the
/// reflection-based <c>JsonSerializer</c> overloads throw, and the failure is easy to miss
/// because it never occurs while debugging in JIT mode. Demanding the source-generated
/// metadata at the call site makes the AOT-unsafe path unreachable by construction.
/// </remarks>
public interface ICacheStore
{
    Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct);

    Task SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo, TimeSpan ttl, CancellationToken ct);

    Task<bool> RemoveAsync(string key, CancellationToken ct);
}
