using Sol.Application.Contracts.Agent;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Realtime;

/// <summary>
/// Sends the agent's known realtime contract without exposing SignalR or an open generic payload
/// surface to the application layer.
/// </summary>
public interface IAgentRealtimeSink
{
    Task SendEventAsync(DeviceId deviceId, AgentEventEnvelope payload, CancellationToken ct);

    Task SendToolCallAsync(
        string connectionId,
        AgentToolCallEnvelope payload,
        CancellationToken ct);

    Task SendApprovalRequestAsync(
        string connectionId,
        AgentApprovalEnvelope payload,
        CancellationToken ct);
}
