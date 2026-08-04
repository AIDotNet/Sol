using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

public sealed class VisitorRepository(NpgsqlDataSource dataSource) : IVisitorRepository
{
    public async Task CreateAsync(VisitorId id, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO visitor (visitor_id, created_at)
            VALUES (@VisitorId, @CreatedAt)
            ON CONFLICT (visitor_id) DO NOTHING
            """,
            new CreateVisitorArgs { VisitorId = id.Value, CreatedAt = createdAt });
    }

    public async Task LinkAsync(DeviceLink link, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // An edge, never a row merge: unlinking a wrong probabilistic guess must cost one
        // DELETE. On conflict the confidence is raised but never lowered, so a later
        // deterministic match upgrades an earlier guess rather than being discarded by it.
        //
        // @Method is cast explicitly: the parameter arrives as int and Npgsql refuses to write
        // an Int32 into a smallint column.
        await connection.ExecuteAsync(
            """
            INSERT INTO device_link (device_id, visitor_id, confidence, method, created_at)
            VALUES (@DeviceId, @VisitorId, @Confidence, @Method::smallint, @CreatedAt)
            ON CONFLICT (device_id, visitor_id) DO UPDATE
            SET confidence = GREATEST(device_link.confidence, EXCLUDED.confidence),
                method     = CASE WHEN EXCLUDED.confidence > device_link.confidence
                                  THEN EXCLUDED.method ELSE device_link.method END
            """,
            new LinkArgs
            {
                DeviceId = link.DeviceId.Value,
                VisitorId = link.VisitorId.Value,
                Confidence = (float)link.Confidence.Value,
                Method = (int)link.Method,
                CreatedAt = link.CreatedAt,
            });
    }

    public async Task<VisitorId?> FindVisitorForDeviceAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // Highest confidence first, so a deterministic link always outranks a probabilistic one.
        var visitorId = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT visitor_id
            FROM device_link
            WHERE device_id = @DeviceId
            ORDER BY confidence DESC, created_at ASC
            LIMIT 1
            """,
            new DeviceIdArg { DeviceId = deviceId.Value });

        return visitorId is { } value ? new VisitorId(value) : null;
    }
}

internal sealed class CreateVisitorArgs
{
    public Guid VisitorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class LinkArgs
{
    public Guid DeviceId { get; init; }
    public Guid VisitorId { get; init; }
    public float Confidence { get; init; }
    public int Method { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
