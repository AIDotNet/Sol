using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

/// <remarks>
/// Every Dapper call here is generated into an interceptor at build time. Stay inside the
/// supported surface: single-generic <c>Query&lt;T&gt;</c>/<c>QueryAsync&lt;T&gt;</c>,
/// <c>Execute</c>, <c>ExecuteScalar</c>, and statically-typed parameter objects. Multi-mapping
/// and <c>QueryMultiple</c> are not supported and would silently fall back to reflection —
/// the DAP001 build gate in Directory.Build.props turns that into an error.
/// </remarks>
public sealed class DeviceRepository(NpgsqlDataSource dataSource) : IDeviceRepository
{
    public async Task<Device?> FindByIdAsync(DeviceId id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<DeviceRow>(
            """
            SELECT device_id, fp_exact, fp_coarse, host(ip_prefix) AS ip_prefix,
                   signals::text AS signals, sig_version, user_agent, first_seen, last_seen
            FROM device
            WHERE device_id = @DeviceId
            """,
            new DeviceIdArg { DeviceId = id.Value });

        return row?.ToDomain();
    }

    public async Task InsertAsync(Device device, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO device (device_id, fp_exact, fp_coarse, ip_prefix, signals,
                                sig_version, user_agent, first_seen, last_seen)
            VALUES (@DeviceId, @FpExact, @FpCoarse, @IpPrefix::inet, @Signals::jsonb,
                    @SigVersion, @UserAgent, @FirstSeen, @LastSeen)
            ON CONFLICT (device_id) DO NOTHING
            """,
            new InsertDeviceArgs
            {
                DeviceId = device.Id.Value,
                FpExact = device.FingerprintExact,
                FpCoarse = device.FingerprintCoarse,
                IpPrefix = device.IpPrefix,
                Signals = device.SignalsJson,
                SigVersion = device.SignalVersion,
                UserAgent = device.UserAgent,
                FirstSeen = device.FirstSeen,
                LastSeen = device.LastSeen,
            });
    }

    public async Task TouchAsync(DeviceId id, DateTimeOffset seenAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            "UPDATE device SET last_seen = @SeenAt WHERE device_id = @DeviceId",
            new TouchDeviceArgs { DeviceId = id.Value, SeenAt = seenAt });
    }

    public async Task<IReadOnlyList<CoarseCandidate>> FindCoarseCandidatesAsync(
        byte[] fingerprintCoarse,
        byte[] excludeFingerprintExact,
        string ipPrefix,
        int signalVersion,
        DateTimeOffset seenSince,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // Devices sharing fp_exact are the same browser and would have presented a cookie,
        // so excluding them keeps this query answering only the cross-browser question.
        //
        // The caller asks for one row beyond its cap so that an over-populated fingerprint is
        // detectable instead of silently truncated — a common coarse hash must block linking,
        // not produce an arbitrary subset.
        var rows = await connection.QueryAsync<CoarseCandidateRow>(
            """
            SELECT d.device_id, dl.visitor_id
            FROM device d
            JOIN device_link dl ON dl.device_id = d.device_id
            WHERE d.fp_coarse   = @FpCoarse
              AND d.ip_prefix   = @IpPrefix::inet
              AND d.sig_version = @SigVersion
              AND d.last_seen  >= @SeenSince
              AND d.fp_exact   <> @ExcludeFpExact
            ORDER BY d.last_seen DESC
            LIMIT @Limit
            """,
            new CoarseCandidateArgs
            {
                FpCoarse = fingerprintCoarse,
                ExcludeFpExact = excludeFingerprintExact,
                IpPrefix = ipPrefix,
                SigVersion = signalVersion,
                SeenSince = seenSince,
                Limit = limit,
            });

        return rows.Select(r => new CoarseCandidate(r.DeviceId, r.VisitorId)).ToList();
    }
}

internal sealed class DeviceIdArg
{
    public Guid DeviceId { get; init; }
}

internal sealed class TouchDeviceArgs
{
    public Guid DeviceId { get; init; }
    public DateTimeOffset SeenAt { get; init; }
}

internal sealed class InsertDeviceArgs
{
    public Guid DeviceId { get; init; }
    public byte[] FpExact { get; init; } = [];
    public byte[] FpCoarse { get; init; } = [];
    public string IpPrefix { get; init; } = string.Empty;
    public string Signals { get; init; } = "{}";
    public int SigVersion { get; init; }
    public string? UserAgent { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

internal sealed class CoarseCandidateArgs
{
    public byte[] FpCoarse { get; init; } = [];
    public byte[] ExcludeFpExact { get; init; } = [];
    public string IpPrefix { get; init; } = string.Empty;
    public int SigVersion { get; init; }
    public DateTimeOffset SeenSince { get; init; }
    public int Limit { get; init; }
}

internal sealed class CoarseCandidateRow
{
    public Guid DeviceId { get; init; }
    public Guid VisitorId { get; init; }
}

internal sealed class DeviceRow
{
    public Guid DeviceId { get; init; }
    public byte[] FpExact { get; init; } = [];
    public byte[] FpCoarse { get; init; } = [];
    public string IpPrefix { get; init; } = string.Empty;
    public string Signals { get; init; } = "{}";
    public int SigVersion { get; init; }
    public string? UserAgent { get; init; }

    // Npgsql materialises timestamptz as a UTC DateTime; declaring DateTimeOffset here makes
    // Dapper attempt a Convert.ChangeType that has no such conversion and throws.
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }

    public Device ToDomain() => new(
        new DeviceId(DeviceId), FpExact, FpCoarse, IpPrefix, Signals, SigVersion, UserAgent,
        new DateTimeOffset(DateTime.SpecifyKind(FirstSeen, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(LastSeen, DateTimeKind.Utc)));
}
