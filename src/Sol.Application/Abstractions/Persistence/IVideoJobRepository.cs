using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

public enum VideoJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record VideoJob(
    Guid JobId,
    DeviceId DeviceId,
    Guid? ProviderId,
    string ModelKey,
    VideoJobStatus Status,
    string? UpstreamJobId,
    double? Progress,
    Guid? AssetId,
    string? Error,
    string RequestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IVideoJobRepository
{
    Task InsertAsync(VideoJob job, CancellationToken ct);

    /// <summary>Scoped by device, so a job id cannot be polled by whoever guesses it.</summary>
    Task<VideoJob?> FindAsync(DeviceId deviceId, Guid jobId, CancellationToken ct);

    /// <summary>Unscoped lookup for the background poller, which runs outside any request.</summary>
    Task<VideoJob?> FindByIdAsync(Guid jobId, CancellationToken ct);

    Task UpdateAsync(VideoJob job, CancellationToken ct);

    /// <summary>
    /// Jobs still in flight, oldest first, for the background poller to advance.
    /// </summary>
    Task<IReadOnlyList<VideoJob>> ListActiveAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Fails jobs that have not been touched since <paramref name="staleBefore"/>.
    /// </summary>
    /// <remarks>
    /// Without this a vendor that silently drops a job leaves a row polling forever. Bounding it
    /// turns an invisible hang into a reported failure.
    /// </remarks>
    Task<int> FailStaleAsync(DateTimeOffset staleBefore, CancellationToken ct);
}
