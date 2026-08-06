namespace Sol.Application.Contracts.Agent;

public sealed record CreateAgentSessionRequest(string CanvasId, string? Title);

public sealed record AgentSessionResponse(
    string Id,
    string CanvasId,
    string? Title,
    string CreatedAt,
    string UpdatedAt);

public sealed record AgentSessionListResponse(AgentSessionResponse[] Sessions);

public sealed record CreateAgentRunRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string? ExecutorConnectionId,
    string[]? Images = null);

public sealed record AgentRunResponse(
    string Id,
    string SessionId,
    string CanvasId,
    string? ProviderId,
    string ModelKey,
    string Status,
    int Iteration,
    long LastSequence,
    string? Error,
    string CreatedAt,
    string UpdatedAt);

public sealed record AgentMessageResponse(
    string Id,
    string? RunId,
    int Ordinal,
    string Role,
    string ContentJson,
    string CreatedAt);

public sealed record AgentMessageListResponse(AgentMessageResponse[] Messages);

public sealed record AgentEventResponse(
    string RunId,
    long Sequence,
    string Type,
    string PayloadJson,
    string CreatedAt);

public sealed record AgentEventListResponse(AgentEventResponse[] Events);

public sealed record AgentToolCallEnvelope(
    string RunId,
    string CanvasId,
    string ToolUseId,
    string ToolName,
    string InputJson,
    int TimeoutMilliseconds);

public sealed record AgentApprovalEnvelope(
    string RunId,
    string ApprovalId,
    string ToolName,
    string Summary,
    string InputJson);

public sealed record AgentToolCatalogEntryResponse(
    string Name,
    string Site,
    string Approval,
    string Description,
    string InputSchemaJson,
    int TimeoutMilliseconds);

public sealed record AgentToolCatalogResponse(AgentToolCatalogEntryResponse[] Tools);

/// <summary>
/// Live output. Sequence is zero for an unpersisted delta and positive for a replayable event.
/// Clients use positive values for gap detection and treat zero as ephemeral display data.
/// </summary>
public sealed record AgentEventEnvelope(
    string RunId,
    long Sequence,
    string Type,
    string? Text,
    string? Json,
    string? Error,
    string SentAt);

public static class AgentRealtimeMethods
{
    public const string AgentEvent = "AgentEvent";
    public const string AgentToolCall = "AgentToolCall";
    public const string AgentApprovalRequest = "AgentApprovalRequest";
}
