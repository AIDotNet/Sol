using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

public sealed class VideoJobRepository(SolConnectionFactory connections) : IVideoJobRepository
{
    private const string Columns = """
        job_id, device_id, provider_id, model_key, status, upstream_job_id,
        progress, asset_id, error, request::text AS request_json, created_at, updated_at
        """;

    public async Task InsertAsync(VideoJob job, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO video_job (job_id, device_id, provider_id, model_key, status,
                                   upstream_job_id, progress, asset_id, error, request,
                                   created_at, updated_at)
            VALUES (@JobId, @DeviceId, @ProviderId, @ModelKey, @Status,
                    @UpstreamJobId, @Progress, @AssetId, @Error, @RequestJson::jsonb,
                    @CreatedAt, @UpdatedAt)
            """,
            new InsertVideoJobParams
            {
                JobId = job.JobId,
                DeviceId = job.DeviceId.Value,
                ProviderId = job.ProviderId,
                ModelKey = job.ModelKey,
                Status = ToWire(job.Status),
                UpstreamJobId = job.UpstreamJobId,
                Progress = job.Progress is { } value ? (float)value : null,
                AssetId = job.AssetId,
                Error = job.Error,
                RequestJson = job.RequestJson,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt,
            });
    }

    public async Task<VideoJob?> FindAsync(DeviceId deviceId, Guid jobId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<VideoJobRow>(
            $"SELECT {Columns} FROM video_job WHERE job_id = @JobId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))",
            new VideoJobScopeParams { JobId = jobId, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    public async Task<VideoJob?> FindByIdAsync(Guid jobId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<VideoJobRow>(
            $"SELECT {Columns} FROM video_job WHERE job_id = @JobId",
            new VideoJobIdParam { JobId = jobId });

        return row?.ToDomain();
    }

    public async Task UpdateAsync(VideoJob job, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            UPDATE video_job
            SET status = @Status, upstream_job_id = @UpstreamJobId, progress = @Progress,
                asset_id = @AssetId, error = @Error, updated_at = @UpdatedAt
            WHERE job_id = @JobId
            """,
            new UpdateVideoJobParams
            {
                JobId = job.JobId,
                Status = ToWire(job.Status),
                UpstreamJobId = job.UpstreamJobId,
                Progress = job.Progress is { } value ? (float)value : null,
                AssetId = job.AssetId,
                Error = job.Error,
                UpdatedAt = job.UpdatedAt,
            });
    }

    public async Task<IReadOnlyList<VideoJob>> ListActiveAsync(int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // Oldest first, so one busy device cannot starve another's jobs.
        var rows = await connection.QueryAsync<VideoJobRow>(
            $"""
            SELECT {Columns}
            FROM video_job
            WHERE status IN ('pending', 'running')
            ORDER BY updated_at
            LIMIT @Limit
            """,
            new LimitParam { Limit = limit });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<int> FailStaleAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteAsync(
            """
            UPDATE video_job
            SET status = 'failed',
                error = 'The provider stopped reporting progress for this job.',
                updated_at = now()
            WHERE status IN ('pending', 'running') AND updated_at < @StaleBefore
            """,
            new StaleParam { StaleBefore = staleBefore });
    }

    private static string ToWire(VideoJobStatus status) => status switch
    {
        VideoJobStatus.Pending => "pending",
        VideoJobStatus.Running => "running",
        VideoJobStatus.Succeeded => "succeeded",
        VideoJobStatus.Failed => "failed",
        VideoJobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };

    internal static VideoJobStatus ParseStatus(string value) => value switch
    {
        "pending" => VideoJobStatus.Pending,
        "running" => VideoJobStatus.Running,
        "succeeded" => VideoJobStatus.Succeeded,
        "failed" => VideoJobStatus.Failed,
        "cancelled" => VideoJobStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unrecognised video job status '{value}'."),
    };
}

internal sealed class VideoJobIdParam
{
    public Guid JobId { get; init; }
}

internal sealed class VideoJobScopeParams
{
    public Guid JobId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class LimitParam
{
    public int Limit { get; init; }
}

internal sealed class StaleParam
{
    public DateTimeOffset StaleBefore { get; init; }
}

internal sealed class InsertVideoJobParams
{
    public Guid JobId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid? ProviderId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Status { get; init; } = "pending";
    public string? UpstreamJobId { get; init; }
    public float? Progress { get; init; }
    public Guid? AssetId { get; init; }
    public string? Error { get; init; }
    public string RequestJson { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateVideoJobParams
{
    public Guid JobId { get; init; }
    public string Status { get; init; } = "pending";
    public string? UpstreamJobId { get; init; }
    public float? Progress { get; init; }
    public Guid? AssetId { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class VideoJobRow
{
    public Guid JobId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid? ProviderId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Status { get; init; } = "pending";
    public string? UpstreamJobId { get; init; }
    public float? Progress { get; init; }
    public Guid? AssetId { get; init; }
    public string? Error { get; init; }
    public string RequestJson { get; init; } = "{}";

    // timestamptz reads back as DateTime — see the note on ProviderRow.
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public VideoJob ToDomain() => new(
        JobId,
        new DeviceId(DeviceId),
        ProviderId,
        ModelKey,
        VideoJobRepository.ParseStatus(Status),
        UpstreamJobId,
        Progress,
        AssetId,
        Error,
        RequestJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
