using Sol.Domain.Identity;

namespace Sol.Domain.Ai;

public readonly record struct AgentSessionId(Guid Value)
{
    public static AgentSessionId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out AgentSessionId id)
    {
        if (Guid.TryParse(text, out var value) && value != Guid.Empty)
        {
            id = new AgentSessionId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public readonly record struct AgentRunId(Guid Value)
{
    public static AgentRunId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out AgentRunId id)
    {
        if (Guid.TryParse(text, out var value) && value != Guid.Empty)
        {
            id = new AgentRunId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public enum AgentRunStatus
{
    Queued,
    Running,
    AwaitingCanvas,
    AwaitingApproval,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted,
}

public sealed record AgentSession(
    AgentSessionId Id,
    DeviceId DeviceId,
    CanvasId CanvasId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentRun(
    AgentRunId Id,
    AgentSessionId SessionId,
    DeviceId DeviceId,
    CanvasId CanvasId,
    ProviderId? ProviderId,
    string ModelKey,
    AgentRunStatus Status,
    string? ExecutorConnectionId,
    int Iteration,
    long LastSequence,
    string? Error,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentStoredMessage(
    Guid MessageId,
    AgentSessionId SessionId,
    AgentRunId? RunId,
    DeviceId DeviceId,
    int Ordinal,
    string Role,
    string ContentJson,
    DateTimeOffset CreatedAt);

public sealed record AgentStoredEvent(
    AgentRunId RunId,
    DeviceId DeviceId,
    long Sequence,
    string Type,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record AgentStoredToolCall(
    Guid ToolCallId,
    AgentRunId RunId,
    DeviceId DeviceId,
    string ToolUseId,
    string ToolName,
    string Site,
    string Status,
    string InputJson,
    string? ResultJson,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
