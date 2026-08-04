using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Caching;

/// <summary>Tracks which devices currently hold live realtime connections.</summary>
public interface IPresenceTracker
{
    Task MarkOnlineAsync(DeviceId deviceId, string connectionId, CancellationToken ct);

    Task MarkOfflineAsync(DeviceId deviceId, string connectionId, CancellationToken ct);

    Task<bool> IsOnlineAsync(DeviceId deviceId, CancellationToken ct);

    Task<long> ConnectionCountAsync(DeviceId deviceId, CancellationToken ct);
}
