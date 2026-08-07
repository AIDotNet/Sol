using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

/// <remarks>
/// The graph crosses the boundary as JSON text and is cast to jsonb in SQL. Npgsql's dynamic
/// JSON mapping is unavailable under AOT, so the conversion stays explicit — the same approach
/// <see cref="McpServerRepository"/> takes for its args/env/headers columns.
/// </remarks>
public sealed class CanvasRepository(SolConnectionFactory connections) : ICanvasRepository
{
    public async Task<IReadOnlyList<CanvasSummary>> ListAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // The node count is derived in SQL so a list request never transfers a graph body.
        // jsonb_array_length throws on a non-array, hence the type guard — a graph written by
        // an older client, or an empty default, must not fail the whole listing.
        var rows = await connection.QueryAsync<CanvasSummaryRow>(
            """
            SELECT canvas_id,
                   name,
                   CASE WHEN jsonb_typeof(graph -> 'nodes') = 'array'
                        THEN jsonb_array_length(graph -> 'nodes')
                        ELSE 0
                   END AS node_count,
                   created_at,
                   updated_at
            FROM canvas
            WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            ORDER BY updated_at DESC
            """,
            new DeviceIdParam { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<Canvas?> FindAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<CanvasRow>(
            """
            SELECT canvas_id, device_id, name, graph::text AS graph_json, created_at, updated_at
            FROM canvas
            WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new CanvasScopeParams { CanvasId = canvasId.Value, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    public async Task InsertAsync(Canvas canvas, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO canvas (canvas_id, device_id, name, graph, created_at, updated_at)
            VALUES (@CanvasId, @DeviceId, @Name, @GraphJson::jsonb, @CreatedAt, @UpdatedAt)
            """,
            new InsertCanvasParams
            {
                CanvasId = canvas.Id.Value,
                DeviceId = canvas.DeviceId.Value,
                Name = canvas.Name,
                GraphJson = canvas.GraphJson,
                CreatedAt = canvas.CreatedAt,
                UpdatedAt = canvas.UpdatedAt,
            });
    }

    public async Task<bool> UpdateAsync(Canvas canvas, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            """
            UPDATE canvas
            SET name = @Name, graph = @GraphJson::jsonb, updated_at = @UpdatedAt
            WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new UpdateCanvasParams
            {
                CanvasId = canvas.Id.Value,
                DeviceId = canvas.DeviceId.Value,
                Name = canvas.Name,
                GraphJson = canvas.GraphJson,
                UpdatedAt = canvas.UpdatedAt,
            });

        return affected > 0;
    }

    public async Task<bool> DeleteAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM canvas WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))",
            new CanvasScopeParams { CanvasId = canvasId.Value, DeviceId = deviceId.Value });

        return affected > 0;
    }
}

internal sealed class CanvasScopeParams
{
    public Guid CanvasId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class InsertCanvasParams
{
    public Guid CanvasId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string GraphJson { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateCanvasParams
{
    public Guid CanvasId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string GraphJson { get; init; } = "{}";
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class CanvasRow
{
    public Guid CanvasId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string GraphJson { get; init; } = "{}";

    // timestamptz reads back as a UTC DateTime; declaring DateTimeOffset on a read row makes
    // Dapper attempt a Convert.ChangeType that does not exist and throw at runtime, with no
    // build error. See ProviderRow.
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public Canvas ToDomain() => new(
        new CanvasId(CanvasId),
        new DeviceId(DeviceId),
        Name,
        GraphJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}

internal sealed class CanvasSummaryRow
{
    public Guid CanvasId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int NodeCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public CanvasSummary ToDomain() => new(
        CanvasId,
        Name,
        NodeCount,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
