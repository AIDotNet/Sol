using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Realtime;

/// <summary>
/// Pushes messages to connected clients.
/// </summary>
/// <remarks>
/// Implemented in the composition root rather than in Infrastructure, because the adapter needs
/// the concrete Hub type and pulling a Hub into Infrastructure would invert the layering.
/// </remarks>
public interface IRealtimeNotifier
{
    Task SendToDeviceAsync<T>(DeviceId deviceId, string method, T payload, CancellationToken ct);

    Task SendToVisitorAsync<T>(VisitorId visitorId, string method, T payload, CancellationToken ct);

    Task BroadcastAsync<T>(string method, T payload, CancellationToken ct);
}
