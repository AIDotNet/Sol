using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Ai;

namespace Sol.Infrastructure.Ai;

/// <summary>
/// PostgreSQL-backed asset store. It makes generated media survive ephemeral cloud containers;
/// the legacy filesystem store is retained as a read/delete fallback for rows created before the
/// cloud blob migration.
/// </summary>
internal sealed class CloudAssetStore(
    NpgsqlDataSource dataSource,
    FileSystemAssetStore legacyFiles)
    : IAssetStore
{
    public async Task<StoredAsset> SaveAsync(
        byte[] bytes,
        string mediaType,
        string extensionHint,
        CancellationToken ct)
    {
        var storagePath = $"cloud/{Guid.CreateVersion7():N}";
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO sol_asset_blob (storage_path, media_type, payload, created_at)
            VALUES (@StoragePath, @MediaType, @Payload, @CreatedAt)
            """,
            new CloudAssetWriteArgs
            {
                StoragePath = storagePath,
                MediaType = mediaType,
                Payload = bytes,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        return new StoredAsset(storagePath, mediaType, bytes.LongLength);
    }

    public async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var payload = await connection.QueryFirstOrDefaultAsync<byte[]>(
            "SELECT payload FROM sol_asset_blob WHERE storage_path = @StoragePath",
            new CloudAssetPathArgs { StoragePath = storagePath });

        if (payload is not null)
        {
            return new MemoryStream(payload, writable: false);
        }

        // A cloud key cannot have a meaningful filesystem fallback. This also avoids probing the
        // legacy directory for a deleted cloud row and keeps normal cloud operations quiet.
        if (IsCloudPath(storagePath)) return null;

        return await legacyFiles.OpenReadAsync(storagePath, ct);
    }

    public async Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM sol_asset_blob WHERE storage_path = @StoragePath",
            new CloudAssetPathArgs { StoragePath = storagePath });
        if (!IsCloudPath(storagePath))
        {
            await legacyFiles.DeleteAsync(storagePath, ct);
        }
    }

    private static bool IsCloudPath(string storagePath) =>
        storagePath.StartsWith("cloud/", StringComparison.Ordinal);
}

internal sealed class CloudAssetWriteArgs
{
    public string StoragePath { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public byte[] Payload { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class CloudAssetPathArgs
{
    public string StoragePath { get; init; } = string.Empty;
}
