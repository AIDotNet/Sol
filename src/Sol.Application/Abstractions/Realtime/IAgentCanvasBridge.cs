using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Realtime;

/// <summary>Waits for a browser executor to perform one canvas-local Agent tool.</summary>
public interface IAgentCanvasBridge
{
    Task<AgentCanvasToolResult> ExecuteAsync(
        AgentRun run,
        string toolUseId,
        string toolName,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct);

    Task<bool> RequestApprovalAsync(
        AgentRun run,
        string approvalId,
        string toolName,
        string summary,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>Re-sends unfinished calls and approvals when a canvas tab takes over a run.</summary>
    Task AttachAsync(AgentRunId runId, string connectionId, CancellationToken ct);

    /// <summary>
    /// Stops addressing calls to a connection that is no longer an executor and returns runs that
    /// are actively waiting for that tab.
    /// </summary>
    IReadOnlyList<AgentRunId> Detach(string connectionId);

    /// <summary>Forgets executor state after a run reaches a terminal status.</summary>
    void Release(AgentRunId runId);

    /// <summary>Completes a pending call. Duplicate submissions are accepted as no-ops.</summary>
    bool Submit(
        AgentRunId runId,
        string toolUseId,
        string resultJson,
        bool isError);

    bool SubmitApproval(AgentRunId runId, string approvalId, bool approved);
}

public sealed record AgentCanvasToolResult(string Json, bool IsError);
