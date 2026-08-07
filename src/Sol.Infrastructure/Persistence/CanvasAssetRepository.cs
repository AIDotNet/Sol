using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

public sealed class CanvasAssetRepository(SolConnectionFactory connections) : ICanvasAssetRepository
{
    public async Task InsertAsync(CanvasAsset asset, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO canvas_asset (asset_id, device_id, kind, media_type, storage_path,
                                      byte_size, prompt, created_at, group_id)
            VALUES (@AssetId, @DeviceId, @Kind, @MediaType, @StoragePath,
                    @ByteSize, @Prompt, @CreatedAt, @GroupId)
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
                GroupId = asset.GroupId,
            });
    }

    public async Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssetRow>(
            """
            SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                   created_at, group_id
            FROM canvas_asset
            WHERE asset_id = @AssetId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
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
        await using var connection = await connections.OpenAsync(ct);

        // Two statements rather than one with `(@Kind IS NULL OR kind = @Kind)`: that form
        // makes the planner choose a single plan for both shapes, and Dapper.AOT prefers a
        // literal predicate it can bind statically.
        var rows = kind is null
            ? await connection.QueryAsync<AssetRow>(
                """
                SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                       created_at, group_id
                FROM canvas_asset
                WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new AssetListParams { DeviceId = deviceId.Value, Limit = limit })
            : await connection.QueryAsync<AssetRow>(
                """
                SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                       created_at, group_id
                FROM canvas_asset
                WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId)) AND kind = @Kind
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
        await using var connection = await connections.OpenAsync(ct);

        // RETURNING makes this one round trip and closes the window where a concurrent delete
        // would leave the caller unsure whether it owns the file it is about to remove.
        return await connection.QueryFirstOrDefaultAsync<string>(
            """
            DELETE FROM canvas_asset
            WHERE asset_id = @AssetId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            RETURNING storage_path
            """,
            new AssetScopeParams { AssetId = assetId, DeviceId = deviceId.Value });
    }

    public async Task<IReadOnlyList<CanvasAssetGroup>> ListGroupsAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<AssetGroupRow>(
            """
            SELECT g.group_id, g.device_id, g.name, g.created_at, g.updated_at,
                   COUNT(a.asset_id)::int AS asset_count
            FROM canvas_asset_group g
            LEFT JOIN canvas_asset a ON a.group_id = g.group_id AND a.device_id = g.device_id
            WHERE g.device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            GROUP BY g.group_id, g.device_id, g.name, g.created_at, g.updated_at
            ORDER BY g.created_at, g.group_id
            """,
            new AssetGroupDeviceParam { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<CanvasAssetGroup?> FindGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssetGroupRow>(
            """
            SELECT g.group_id, g.device_id, g.name, g.created_at, g.updated_at,
                   COUNT(a.asset_id)::int AS asset_count
            FROM canvas_asset_group g
            LEFT JOIN canvas_asset a ON a.group_id = g.group_id AND a.device_id = g.device_id
            WHERE g.group_id = @GroupId AND g.device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            GROUP BY g.group_id, g.device_id, g.name, g.created_at, g.updated_at
            """,
            new GroupScopeParams { GroupId = groupId, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    public async Task<CanvasAssetGroup?> InsertGroupAsync(
        CanvasAssetGroup group,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // The unique index is case-insensitive. Returning the row lets the endpoint turn a name
        // collision into a useful 409 without a second query.
        var row = await connection.QueryFirstOrDefaultAsync<AssetGroupRow>(
            """
            INSERT INTO canvas_asset_group (group_id, device_id, name, created_at, updated_at)
            VALUES (@GroupId, @DeviceId, @Name, @CreatedAt, @UpdatedAt)
            ON CONFLICT DO NOTHING
            RETURNING group_id, device_id, name, created_at, updated_at, 0::int AS asset_count
            """,
            new InsertGroupParams
            {
                GroupId = group.GroupId,
                DeviceId = group.DeviceId.Value,
                Name = group.Name,
                CreatedAt = group.CreatedAt,
                UpdatedAt = group.UpdatedAt,
            });

        return row?.ToDomain();
    }

    public async Task<CanvasAssetGroup?> RenameGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        string name,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // The NOT EXISTS predicate makes a duplicate name a normal null result rather than a
        // database exception, while the second query keeps the current asset count intact.
        var changed = await connection.ExecuteAsync(
            """
            UPDATE canvas_asset_group AS g
            SET name = @Name, updated_at = @UpdatedAt
            WHERE g.group_id = @GroupId AND g.device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
              AND NOT EXISTS (
                  SELECT 1
                  FROM canvas_asset_group other
                  WHERE other.device_id = g.device_id
                    AND other.group_id <> g.group_id
                    AND lower(other.name) = lower(@Name)
              )
            """,
            new RenameGroupParams
            {
                GroupId = groupId,
                DeviceId = deviceId.Value,
                Name = name,
                UpdatedAt = updatedAt,
            });

        return changed == 0 ? null : await FindGroupAsync(deviceId, groupId, ct);
    }

    public async Task<bool> DeleteGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            """
            DELETE FROM canvas_asset_group
            WHERE group_id = @GroupId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new GroupScopeParams { GroupId = groupId, DeviceId = deviceId.Value });

        return affected > 0;
    }

    public async Task<int> AssignGroupAsync(
        DeviceId deviceId,
        IReadOnlyList<Guid> assetIds,
        Guid? groupId,
        CancellationToken ct)
    {
        if (assetIds.Count == 0) return 0;

        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteAsync(
            """
            UPDATE canvas_asset
            SET group_id = @GroupId
            WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId)) AND asset_id = ANY(@AssetIds)
            """,
            new AssignGroupParams
            {
                DeviceId = deviceId.Value,
                AssetIds = [.. assetIds],
                GroupId = groupId,
            });
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
    public Guid? GroupId { get; init; }
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
    public Guid? GroupId { get; init; }

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
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        GroupId);
}

internal sealed class AssetGroupDeviceParam
{
    public Guid DeviceId { get; init; }
}

internal sealed class GroupScopeParams
{
    public Guid GroupId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class InsertGroupParams
{
    public Guid GroupId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class RenameGroupParams
{
    public Guid GroupId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class AssignGroupParams
{
    public Guid DeviceId { get; init; }
    public Guid[] AssetIds { get; init; } = [];
    public Guid? GroupId { get; init; }
}

internal sealed class AssetGroupRow
{
    public Guid GroupId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int AssetCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public CanvasAssetGroup ToDomain() => new(
        GroupId,
        new DeviceId(DeviceId),
        Name,
        AssetCount,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
