using System.Collections.Concurrent;
using Sol.Application.Abstractions.Realtime;
using Sol.Application.Contracts.Agent;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

/// <summary>In-process reverse-RPC rendezvous between an Agent worker and one browser tab.</summary>
/// <remarks>
/// The pending registry is intentionally process-local. Agent execution is single-instance until a
/// distributed result relay is implemented; a Redis SignalR backplane alone does not move a waiting
/// <see cref="TaskCompletionSource{TResult}"/> between API processes.
/// </remarks>
internal sealed class AgentCanvasBridge(IAgentRealtimeSink realtime) : IAgentCanvasBridge
{
    private readonly ConcurrentDictionary<PendingKey, PendingCall> _pending = new();
    private readonly ConcurrentDictionary<PendingKey, PendingApproval> _approvals = new();
    private readonly ConcurrentDictionary<Guid, string> _executors = new();
    private readonly object _executorGate = new();

    public async Task<AgentCanvasToolResult> ExecuteAsync(
        AgentRun run,
        string toolUseId,
        string toolName,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var payload = new AgentToolCallEnvelope(
            run.Id.ToString(),
            run.CanvasId.ToString(),
            toolUseId,
            toolName,
            inputJson,
            checked((int)Math.Clamp(timeout.TotalMilliseconds, 1_000, int.MaxValue)));
        var connectionId = _executors.TryGetValue(run.Id.Value, out var attached)
            ? attached
            : run.ExecutorConnectionId;
        var call = new PendingCall(payload, connectionId);
        var key = new PendingKey(run.Id.Value, toolUseId);
        if (!_pending.TryAdd(key, call))
        {
            return new AgentCanvasToolResult(
                "{\"error\":\"duplicate_tool_use_id\"}", IsError: true);
        }

        try
        {
            await SendIfAttachedAsync(call, ct);
            try
            {
                return await call.Completion.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                return new AgentCanvasToolResult(
                    "{\"error\":\"canvas_tool_timeout\"}", IsError: true);
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public async Task<bool> RequestApprovalAsync(
        AgentRun run,
        string approvalId,
        string toolName,
        string summary,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var payload = new AgentApprovalEnvelope(
            run.Id.ToString(), approvalId, toolName, summary, inputJson);
        var connectionId = _executors.TryGetValue(run.Id.Value, out var attached)
            ? attached
            : run.ExecutorConnectionId;
        var approval = new PendingApproval(payload, connectionId);
        var key = new PendingKey(run.Id.Value, approvalId);
        if (!_approvals.TryAdd(key, approval)) return false;

        try
        {
            await SendApprovalIfAttachedAsync(approval, ct);
            try
            {
                return await approval.Completion.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            _approvals.TryRemove(key, out _);
        }
    }

    public async Task AttachAsync(AgentRunId runId, string connectionId, CancellationToken ct)
    {
        lock (_executorGate)
        {
            // The browser periodically re-claims its executor slot while a run is active. A
            // repeated claim from the same SignalR connection is only a heartbeat; replaying all
            // pending calls here would execute the same canvas mutation more than once.
            if (_executors.TryGetValue(runId.Value, out var current)
                && string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                return;
            }

            _executors[runId.Value] = connectionId;
        }

        foreach (var pair in _pending)
        {
            if (pair.Key.RunId != runId.Value) continue;
            lock (pair.Value.Gate)
            {
                pair.Value.ConnectionId = connectionId;
            }
            await SendIfAttachedAsync(pair.Value, ct);
        }
        foreach (var pair in _approvals)
        {
            if (pair.Key.RunId != runId.Value) continue;
            lock (pair.Value.Gate)
            {
                pair.Value.ConnectionId = connectionId;
            }
            await SendApprovalIfAttachedAsync(pair.Value, ct);
        }
    }

    public IReadOnlyList<AgentRunId> Detach(string connectionId)
    {
        var runs = new HashSet<Guid>();
        foreach (var executor in _executors)
        {
            if (string.Equals(executor.Value, connectionId, StringComparison.Ordinal)
                && _executors.TryRemove(executor))
            {
                runs.Add(executor.Key);
            }
        }

        foreach (var pair in _pending)
        {
            lock (pair.Value.Gate)
            {
                if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    pair.Value.ConnectionId = null;
                    runs.Add(pair.Key.RunId);
                }
            }
        }
        foreach (var pair in _approvals)
        {
            lock (pair.Value.Gate)
            {
                if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    pair.Value.ConnectionId = null;
                    runs.Add(pair.Key.RunId);
                }
            }
        }
        return runs.Select(value => new AgentRunId(value)).ToList();
    }

    public void Release(AgentRunId runId) => _executors.TryRemove(runId.Value, out _);

    public bool Submit(
        AgentRunId runId,
        string toolUseId,
        string resultJson,
        bool isError)
    {
        var key = new PendingKey(runId.Value, toolUseId);
        if (!_pending.TryGetValue(key, out var call)) return true;
        _ = call.Completion.TrySetResult(new AgentCanvasToolResult(resultJson, isError));
        return true;
    }

    public bool SubmitApproval(AgentRunId runId, string approvalId, bool approved)
    {
        var key = new PendingKey(runId.Value, approvalId);
        if (!_approvals.TryGetValue(key, out var approval)) return true;
        _ = approval.Completion.TrySetResult(approved);
        return true;
    }

    private async Task SendIfAttachedAsync(PendingCall call, CancellationToken ct)
    {
        string? connectionId;
        lock (call.Gate)
        {
            connectionId = call.ConnectionId;
        }
        if (connectionId is null) return;
        await realtime.SendToolCallAsync(connectionId, call.Payload, ct);
    }

    private async Task SendApprovalIfAttachedAsync(PendingApproval approval, CancellationToken ct)
    {
        string? connectionId;
        lock (approval.Gate)
        {
            connectionId = approval.ConnectionId;
        }
        if (connectionId is null) return;
        await realtime.SendApprovalRequestAsync(connectionId, approval.Payload, ct);
    }

    private readonly record struct PendingKey(Guid RunId, string ToolUseId);

    private sealed class PendingCall(AgentToolCallEnvelope payload, string? connectionId)
    {
        public object Gate { get; } = new();
        public AgentToolCallEnvelope Payload { get; } = payload;
        public string? ConnectionId { get; set; } = connectionId;
        public TaskCompletionSource<AgentCanvasToolResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingApproval(AgentApprovalEnvelope payload, string? connectionId)
    {
        public object Gate { get; } = new();
        public AgentApprovalEnvelope Payload { get; } = payload;
        public string? ConnectionId { get; set; } = connectionId;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
