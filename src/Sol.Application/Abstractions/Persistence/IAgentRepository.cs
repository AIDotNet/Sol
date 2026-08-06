using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

/// <summary>
/// Durable agent conversations and runs. Every request-facing operation carries a device id so
/// ownership is enforced in SQL rather than by a post-query check a caller could omit.
/// </summary>
public interface IAgentRepository
{
    Task<AgentSession?> FindSessionAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    Task<AgentSession?> FindSessionByCanvasAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct);

    Task InsertSessionAsync(AgentSession session, CancellationToken ct);

    Task<AgentRun?> FindRunAsync(DeviceId deviceId, AgentRunId runId, CancellationToken ct);

    /// <summary>Unscoped lookup for the background host, which runs outside an HTTP request.</summary>
    Task<AgentRun?> FindRunByIdAsync(AgentRunId runId, CancellationToken ct);

    Task<AgentRun?> FindActiveRunAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct);

    Task InsertRunAsync(AgentRun run, CancellationToken ct);

    Task<bool> UpdateRunAsync(AgentRun run, CancellationToken ct);

    Task<bool> ClaimExecutorConnectionAsync(
        DeviceId deviceId,
        AgentRunId runId,
        CanvasId canvasId,
        string connectionId,
        CancellationToken ct);

    Task<int> ClearExecutorConnectionAsync(
        DeviceId deviceId,
        string connectionId,
        CancellationToken ct);

    Task<IReadOnlyList<AgentRunId>> ListQueuedAsync(int limit, CancellationToken ct);

    Task<IReadOnlyList<AgentStoredMessage>> ListMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    Task<int> ClearMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    Task InsertMessageAsync(AgentStoredMessage message, CancellationToken ct);

    Task<int> NextMessageOrdinalAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    Task InsertToolCallAsync(AgentStoredToolCall toolCall, CancellationToken ct);

    Task<bool> CompleteToolCallAsync(
        DeviceId deviceId,
        AgentRunId runId,
        string toolUseId,
        string status,
        string resultJson,
        string? error,
        DateTimeOffset finishedAt,
        CancellationToken ct);

    /// <summary>
    /// Atomically advances a run's sequence and inserts its replayable event.
    /// </summary>
    Task<AgentStoredEvent?> AppendEventAsync(
        AgentRunId runId,
        DeviceId deviceId,
        string type,
        string payloadJson,
        CancellationToken ct);

    Task<IReadOnlyList<AgentStoredEvent>> ListEventsAsync(
        DeviceId deviceId,
        AgentRunId runId,
        long afterSequence,
        CancellationToken ct);

    Task<int> InterruptOrphanedRunsAsync(DateTimeOffset now, CancellationToken ct);
}
