using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

public interface IVisitorRepository
{
    Task CreateAsync(VisitorId id, DateTimeOffset createdAt, CancellationToken ct);

    /// <summary>Adds a device-to-visitor edge. Idempotent on (device, visitor).</summary>
    Task LinkAsync(DeviceLink link, CancellationToken ct);

    /// <summary>
    /// Returns the visitor a device belongs to, preferring the highest-confidence edge so a
    /// deterministic link always wins over a probabilistic one.
    /// </summary>
    Task<VisitorId?> FindVisitorForDeviceAsync(DeviceId deviceId, CancellationToken ct);
}
