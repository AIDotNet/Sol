using Microsoft.AspNetCore.SignalR;
using Sol.Api.Hubs;
using Sol.Api.Serialization;
using Sol.Application.Abstractions.Realtime;
using Sol.Application.Contracts.Agent;
using Sol.Domain.Identity;

namespace Sol.Api.Realtime;

/// <summary>
/// Sends realtime messages on behalf of the application layer.
/// </summary>
/// <remarks>
/// Lives in the composition root because it needs the concrete <see cref="SolHub"/> type;
/// placing it in Infrastructure would drag a delivery-layer type inward. Uses
/// <c>IHubContext&lt;SolHub&gt;</c> — the non-generic form. The two-parameter
/// <c>IHubContext&lt;THub, TClient&gt;</c> generates its proxy at runtime and is unusable under AOT.
/// </remarks>
public sealed class SignalRNotifier(IHubContext<SolHub> hub) : IRealtimeNotifier, IAgentRealtimeSink
{
    public Task SendToDeviceAsync<T>(DeviceId deviceId, string method, T payload, CancellationToken ct) =>
        hub.Clients.Group(HubGroups.ForDevice(deviceId)).SendAsync(method, payload, ct);

    public Task SendToVisitorAsync<T>(VisitorId visitorId, string method, T payload, CancellationToken ct) =>
        hub.Clients.Group(HubGroups.ForVisitor(visitorId)).SendAsync(method, payload, ct);

    public Task BroadcastAsync<T>(string method, T payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync(method, payload, ct);

    public Task SendEventAsync(
        DeviceId deviceId,
        AgentEventEnvelope payload,
        CancellationToken ct) =>
        hub.Clients.Group(HubGroups.ForDevice(deviceId))
            .SendAsync(AgentRealtimeMethods.AgentEvent, payload, ct);

    public Task SendToolCallAsync(
        string connectionId,
        AgentToolCallEnvelope payload,
        CancellationToken ct) =>
        hub.Clients.Client(connectionId)
            .SendAsync(AgentRealtimeMethods.AgentToolCall, payload, ct);

    public Task SendApprovalRequestAsync(
        string connectionId,
        AgentApprovalEnvelope payload,
        CancellationToken ct) =>
        hub.Clients.Client(connectionId)
            .SendAsync(AgentRealtimeMethods.AgentApprovalRequest, payload, ct);
}
