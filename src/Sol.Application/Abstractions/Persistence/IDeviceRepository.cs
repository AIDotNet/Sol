using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

public interface IDeviceRepository
{
    Task<Device?> FindByIdAsync(DeviceId id, CancellationToken ct);

    Task InsertAsync(Device device, CancellationToken ct);

    Task TouchAsync(DeviceId id, DateTimeOffset seenAt, CancellationToken ct);

    /// <summary>
    /// Finds devices that might be the same physical machine seen through a different browser.
    /// </summary>
    /// <remarks>
    /// Callers must inspect the whole result, not just the first row. A single distinct visitor
    /// within a small candidate set is weak evidence of one device; a large set means the coarse
    /// fingerprint is simply common (shared NAT egress, identical corporate laptops) and must not
    /// be linked at all. Rows whose <c>fp_exact</c> equals the caller's are excluded — identical
    /// exact fingerprints mean the same browser, which would have presented a cookie.
    /// </remarks>
    Task<IReadOnlyList<CoarseCandidate>> FindCoarseCandidatesAsync(
        byte[] fingerprintCoarse,
        byte[] excludeFingerprintExact,
        string ipPrefix,
        int signalVersion,
        DateTimeOffset seenSince,
        int limit,
        CancellationToken ct);
}

/// <summary>A device that shares a coarse fingerprint, plus the visitor it currently belongs to.</summary>
public sealed record CoarseCandidate(Guid DeviceId, Guid VisitorId);
