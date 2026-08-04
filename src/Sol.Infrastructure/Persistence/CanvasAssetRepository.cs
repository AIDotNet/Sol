using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

public sealed class CanvasAssetRepository(NpgsqlDataSource dataSource) : ICanvasAssetRepository
{
    public async Task InsertAsync(CanvasAsset asset, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO canvas_asset (asset_id, device_id, kind, media_type, storage_path,
                                      byte_size, prompt, created_at)
            VALUES (@AssetId, @DeviceId, @Kind, @MediaType, @StoragePath,
                    @ByteSize, @Prompt, @CreatedAt)
            """,
            new InsertAssetParams
            {
                AssetId = asset.AssetId,
                DeviceId = asset.DeviceId.Value,
                Kind = asset.Kind,
                MediaType = asset.MediaType,
                StoragePath = asset.StoragePath,
                ByteSize = asset.ByteSize,
                Prompt = asset.Prompt,
                CreatedAt = asset.CreatedAt,
            });
    }

    public async Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssetRow>(
            """
            SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt, created_at
            FROM canvas_asset
            WHERE asset_id = @AssetId AND device_id = @DeviceId
            """,
            new AssetScopeParams { AssetId = assetId, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<CanvasAsset>> ListAsync(
        DeviceId deviceId,
        string? kind,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // Two statements rather than one with `(@Kind IS NULL OR kind = @Kind)`: that form
        // makes the planner choose a single plan for both shapes, and Dapper.AOT prefers a
        // literal predicate it can bind statically.
        var rows = kind is null
            ? await connection.QueryAsync<AssetRow>(
                """
                SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                       created_at
                FROM canvas_asset
                WHERE device_id = @DeviceId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new AssetListParams { DeviceId = deviceId.Value, Limit = limit })
            : await connection.QueryAsync<AssetRow>(
                """
                SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                       created_at
                FROM canvas_asset
                WHERE device_id = @DeviceId AND kind = @Kind
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new AssetListByKindParams
                {
                    DeviceId = deviceId.Value,
                    Kind = kind,
                    Limit = limit,
                });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<string?> DeleteAsync(DeviceId deviceId, Guid assetId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // RETURNING makes this one round trip and closes the window where a concurrent delete
        // would leave the caller unsure whether it owns the file it is about to remove.
        return await connection.QueryFirstOrDefaultAsync<string>(
            """
            DELETE FROM canvas_asset
            WHERE asset_id = @AssetId AND device_id = @DeviceId
            RETURNING storage_path
            """,
            new AssetScopeParams { AssetId = assetId, DeviceId = deviceId.Value });
    }
}

internal sealed class AssetListParams
{
    public Guid DeviceId { get; init; }
    public int Limit { get; init; }
}

internal sealed class AssetListByKindParams
{
    public Guid DeviceId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public int Limit { get; init; }
}

internal sealed class InsertAssetParams
{
    public Guid AssetId { get; init; }
    public Guid DeviceId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public long ByteSize { get; init; }
    public string? Prompt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AssetScopeParams
{
    public Guid AssetId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class AssetRow
{
    public Guid AssetId { get; init; }
    public Guid DeviceId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public long ByteSize { get; init; }
    public string? Prompt { get; init; }

    // timestamptz reads back as DateTime — see the note on ProviderRow.
    public DateTime CreatedAt { get; init; }

    public CanvasAsset ToDomain() => new(
        AssetId,
        new DeviceId(DeviceId),
        Kind,
        MediaType,
        StoragePath,
        ByteSize,
        Prompt,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}
