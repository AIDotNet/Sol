using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

/// <summary>
/// Stores agent conversations and runs. JSON content crosses Npgsql as text and is explicitly cast
/// to jsonb, matching <see cref="CanvasRepository"/> and avoiding dynamic JSON mapping under AOT.
/// </summary>
public sealed class AgentRepository(NpgsqlDataSource dataSource) : IAgentRepository
{
    private const string SessionColumns =
        "session_id, device_id, canvas_id, title, created_at, updated_at";

    private const string RunColumns = """
        run_id, session_id, device_id, canvas_id, provider_id, model_key, status,
        executor_connection_id, iteration, last_seq, error, input_tokens, output_tokens,
        started_at, finished_at, created_at, updated_at
        """;

    public async Task<AgentSession?> FindSessionAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentSessionRow>(
            $"SELECT {SessionColumns} FROM agent_session WHERE session_id = @SessionId AND device_id = @DeviceId",
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    public async Task<AgentSession?> FindSessionByCanvasAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentSessionRow>(
            $"SELECT {SessionColumns} FROM agent_session WHERE canvas_id = @CanvasId AND device_id = @DeviceId",
            new AgentCanvasScopeParams { CanvasId = canvasId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    public async Task InsertSessionAsync(AgentSession session, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_session
                (session_id, device_id, canvas_id, title, created_at, updated_at)
            VALUES (@SessionId, @DeviceId, @CanvasId, @Title, @CreatedAt, @UpdatedAt)
            """,
            new InsertAgentSessionParams
            {
                SessionId = session.Id.Value,
                DeviceId = session.DeviceId.Value,
                CanvasId = session.CanvasId.Value,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
            });
    }

    public async Task<AgentRun?> FindRunAsync(DeviceId deviceId, AgentRunId runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentRunRow>(
            $"SELECT {RunColumns} FROM agent_run WHERE run_id = @RunId AND device_id = @DeviceId",
            new AgentRunScopeParams { RunId = runId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    public async Task<AgentRun?> FindRunByIdAsync(AgentRunId runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentRunRow>(
            $"SELECT {RunColumns} FROM agent_run WHERE run_id = @RunId",
            new AgentRunIdParams { RunId = runId.Value });
        return row?.ToDomain();
    }

    public async Task<AgentRun?> FindActiveRunAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentRunRow>(
            $"""
            SELECT {RunColumns}
            FROM agent_run
            WHERE device_id = @DeviceId AND canvas_id = @CanvasId
              AND status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval')
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new AgentCanvasScopeParams { DeviceId = deviceId.Value, CanvasId = canvasId.Value });
        return row?.ToDomain();
    }

    public async Task InsertRunAsync(AgentRun run, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_run
                (run_id, session_id, device_id, canvas_id, provider_id, model_key, status,
                 executor_connection_id, iteration, last_seq, error, input_tokens, output_tokens,
                 started_at, finished_at, created_at, updated_at)
            VALUES
                (@RunId, @SessionId, @DeviceId, @CanvasId, @ProviderId, @ModelKey, @Status,
                 @ExecutorConnectionId, @Iteration::smallint, @LastSequence, @Error,
                 @InputTokens, @OutputTokens, @StartedAt, @FinishedAt, @CreatedAt, @UpdatedAt)
            """,
            AgentRunParams.From(run));
    }

    public async Task<bool> UpdateRunAsync(AgentRun run, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET status = @Status,
                iteration = @Iteration::smallint,
                error = @Error,
                input_tokens = @InputTokens,
                output_tokens = @OutputTokens,
                started_at = @StartedAt,
                finished_at = @FinishedAt,
                updated_at = @UpdatedAt
            WHERE run_id = @RunId AND device_id = @DeviceId
            """,
            AgentRunParams.From(run));
        return affected > 0;
    }

    public async Task<bool> ClaimExecutorConnectionAsync(
        DeviceId deviceId,
        AgentRunId runId,
        CanvasId canvasId,
        string connectionId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET executor_connection_id = @ConnectionId,
                status = CASE
                    WHEN status = 'awaiting_canvas' THEN 'running'
                    ELSE status
                END,
                updated_at = @Now
            WHERE run_id = @RunId AND device_id = @DeviceId AND canvas_id = @CanvasId
              AND status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval')
              AND (executor_connection_id IS NULL OR executor_connection_id = @ConnectionId)
            """,
            new ClaimAgentExecutorParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                CanvasId = canvasId.Value,
                ConnectionId = connectionId,
                Now = DateTimeOffset.UtcNow,
            });
        return affected > 0;
    }

    public async Task<int> ClearExecutorConnectionAsync(
        DeviceId deviceId,
        string connectionId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET executor_connection_id = NULL,
                status = CASE
                    WHEN status = 'queued' THEN status
                    ELSE 'awaiting_canvas'
                END,
                updated_at = @Now
            WHERE device_id = @DeviceId AND executor_connection_id = @ConnectionId
              AND status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval')
            """,
            new ClearAgentExecutorParams
            {
                DeviceId = deviceId.Value,
                ConnectionId = connectionId,
                Now = DateTimeOffset.UtcNow,
            });
    }

    public async Task<IReadOnlyList<AgentRunId>> ListQueuedAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<QueuedAgentRunRow>(
            """
            SELECT run_id
            FROM agent_run
            WHERE status = 'queued'
            ORDER BY created_at
            LIMIT @Limit
            """,
            new AgentLimitParams { Limit = limit });
        return rows.Select(row => new AgentRunId(row.RunId)).ToList();
    }

    public async Task<IReadOnlyList<AgentStoredMessage>> ListMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AgentMessageRow>(
            """
            SELECT message_id, session_id, run_id, device_id, ordinal, role,
                   content::text AS content_json, created_at
            FROM agent_message
            WHERE session_id = @SessionId AND device_id = @DeviceId
            ORDER BY ordinal
            """,
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<int> ClearMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(
            """
            DELETE FROM agent_message
            WHERE session_id = @SessionId AND device_id = @DeviceId
            """,
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
    }

    public async Task InsertMessageAsync(AgentStoredMessage message, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_message
                (message_id, session_id, run_id, device_id, ordinal, role, content, created_at)
            VALUES
                (@MessageId, @SessionId, @RunId, @DeviceId, @Ordinal, @Role,
                 @ContentJson::jsonb, @CreatedAt)
            """,
            new InsertAgentMessageParams
            {
                MessageId = message.MessageId,
                SessionId = message.SessionId.Value,
                RunId = message.RunId?.Value,
                DeviceId = message.DeviceId.Value,
                Ordinal = message.Ordinal,
                Role = message.Role,
                ContentJson = message.ContentJson,
                CreatedAt = message.CreatedAt,
            });
    }

    public async Task<int> NextMessageOrdinalAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstAsync<AgentOrdinalRow>(
            """
            SELECT COALESCE(MAX(ordinal), -1) + 1 AS ordinal
            FROM agent_message
            WHERE session_id = @SessionId AND device_id = @DeviceId
            """,
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
        return row.Ordinal;
    }

    public async Task InsertToolCallAsync(AgentStoredToolCall toolCall, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_tool_call
                (tool_call_id, run_id, device_id, tool_use_id, tool_name, site, status,
                 input, result, error, started_at, finished_at)
            VALUES
                (@ToolCallId, @RunId, @DeviceId, @ToolUseId, @ToolName, @Site, @Status,
                 @InputJson::jsonb, @ResultJson::jsonb, @Error, @StartedAt, @FinishedAt)
            ON CONFLICT (run_id, tool_use_id) DO NOTHING
            """,
            AgentToolCallParams.From(toolCall));
    }

    public async Task<bool> CompleteToolCallAsync(
        DeviceId deviceId,
        AgentRunId runId,
        string toolUseId,
        string status,
        string resultJson,
        string? error,
        DateTimeOffset finishedAt,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE agent_tool_call
            SET status = @Status,
                result = @ResultJson::jsonb,
                error = @Error,
                finished_at = @FinishedAt
            WHERE run_id = @RunId AND device_id = @DeviceId AND tool_use_id = @ToolUseId
            """,
            new CompleteAgentToolCallParams
            {
                DeviceId = deviceId.Value,
                RunId = runId.Value,
                ToolUseId = toolUseId,
                Status = status,
                ResultJson = resultJson,
                Error = error,
                FinishedAt = finishedAt,
            });
        return affected > 0;
    }

    public async Task<AgentStoredEvent?> AppendEventAsync(
        AgentRunId runId,
        DeviceId deviceId,
        string type,
        string payloadJson,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var sequence = await connection.QueryFirstOrDefaultAsync<AgentSequenceRow>(
            """
            UPDATE agent_run
            SET last_seq = last_seq + 1, updated_at = @CreatedAt
            WHERE run_id = @RunId AND device_id = @DeviceId
            RETURNING last_seq AS sequence
            """,
            new AppendAgentEventParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                Type = type,
                PayloadJson = payloadJson,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            transaction);

        if (sequence is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var createdAt = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_event (run_id, device_id, seq, type, payload, created_at)
            VALUES (@RunId, @DeviceId, @Sequence, @Type, @PayloadJson::jsonb, @CreatedAt)
            """,
            new InsertAgentEventParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                Sequence = sequence.Sequence,
                Type = type,
                PayloadJson = payloadJson,
                CreatedAt = createdAt,
            },
            transaction);

        await transaction.CommitAsync(ct);
        return new AgentStoredEvent(runId, deviceId, sequence.Sequence, type, payloadJson, createdAt);
    }

    public async Task<IReadOnlyList<AgentStoredEvent>> ListEventsAsync(
        DeviceId deviceId,
        AgentRunId runId,
        long afterSequence,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AgentEventRow>(
            """
            SELECT event.run_id, event.device_id, event.seq, event.type,
                   event.payload::text AS payload_json, event.created_at
            FROM agent_event event
            JOIN agent_run run ON run.run_id = event.run_id
            WHERE event.run_id = @RunId AND event.device_id = @DeviceId
              AND run.device_id = @DeviceId AND event.seq > @AfterSequence
            ORDER BY event.seq
            LIMIT 1000
            """,
            new AgentEventScopeParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                AfterSequence = Math.Max(0, afterSequence),
            });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<int> InterruptOrphanedRunsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET status = 'interrupted',
                error = 'The server restarted while this run was active.',
                finished_at = @Now,
                updated_at = @Now
            WHERE status IN ('running', 'awaiting_canvas', 'awaiting_approval')
            """,
            new AgentNowParams { Now = now });
    }

    internal static string ToWire(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Queued => "queued",
        AgentRunStatus.Running => "running",
        AgentRunStatus.AwaitingCanvas => "awaiting_canvas",
        AgentRunStatus.AwaitingApproval => "awaiting_approval",
        AgentRunStatus.Succeeded => "succeeded",
        AgentRunStatus.Failed => "failed",
        AgentRunStatus.Cancelled => "cancelled",
        AgentRunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown agent run status."),
    };

    internal static AgentRunStatus ParseStatus(string status) => status switch
    {
        "queued" => AgentRunStatus.Queued,
        "running" => AgentRunStatus.Running,
        "awaiting_canvas" => AgentRunStatus.AwaitingCanvas,
        "awaiting_approval" => AgentRunStatus.AwaitingApproval,
        "succeeded" => AgentRunStatus.Succeeded,
        "failed" => AgentRunStatus.Failed,
        "cancelled" => AgentRunStatus.Cancelled,
        "interrupted" => AgentRunStatus.Interrupted,
        _ => throw new InvalidOperationException($"Unrecognised agent run status '{status}'."),
    };
}

internal sealed class AgentSessionScopeParams { public Guid SessionId { get; init; } public Guid DeviceId { get; init; } }
internal sealed class AgentCanvasScopeParams { public Guid CanvasId { get; init; } public Guid DeviceId { get; init; } }
internal sealed class AgentRunScopeParams { public Guid RunId { get; init; } public Guid DeviceId { get; init; } }
internal sealed class AgentRunIdParams { public Guid RunId { get; init; } }
internal sealed class AgentLimitParams { public int Limit { get; init; } }
internal sealed class AgentNowParams { public DateTimeOffset Now { get; init; } }
internal sealed class ClaimAgentExecutorParams
{
    public Guid RunId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid CanvasId { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public DateTimeOffset Now { get; init; }
}
internal sealed class ClearAgentExecutorParams
{
    public Guid DeviceId { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public DateTimeOffset Now { get; init; }
}
internal sealed class AgentEventScopeParams { public Guid RunId { get; init; } public Guid DeviceId { get; init; } public long AfterSequence { get; init; } }
internal sealed class AgentOrdinalRow { public int Ordinal { get; init; } }
internal sealed class AgentSequenceRow { public long Sequence { get; init; } }
internal sealed class QueuedAgentRunRow { public Guid RunId { get; init; } }

internal sealed class InsertAgentSessionParams
{
    public Guid SessionId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid CanvasId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class AgentRunParams
{
    public Guid RunId { get; init; }
    public Guid SessionId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid CanvasId { get; init; }
    public Guid? ProviderId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public string? ExecutorConnectionId { get; init; }
    public int Iteration { get; init; }
    public long LastSequence { get; init; }
    public string? Error { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public static AgentRunParams From(AgentRun run) => new()
    {
        RunId = run.Id.Value,
        SessionId = run.SessionId.Value,
        DeviceId = run.DeviceId.Value,
        CanvasId = run.CanvasId.Value,
        ProviderId = run.ProviderId?.Value,
        ModelKey = run.ModelKey,
        Status = AgentRepository.ToWire(run.Status),
        ExecutorConnectionId = run.ExecutorConnectionId,
        Iteration = run.Iteration,
        LastSequence = run.LastSequence,
        Error = run.Error,
        InputTokens = run.InputTokens,
        OutputTokens = run.OutputTokens,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
    };
}

internal sealed class InsertAgentMessageParams
{
    public Guid MessageId { get; init; }
    public Guid SessionId { get; init; }
    public Guid? RunId { get; init; }
    public Guid DeviceId { get; init; }
    public int Ordinal { get; init; }
    public string Role { get; init; } = "user";
    public string ContentJson { get; init; } = "[]";
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AppendAgentEventParams
{
    public Guid RunId { get; init; }
    public Guid DeviceId { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AgentToolCallParams
{
    public Guid ToolCallId { get; init; }
    public Guid RunId { get; init; }
    public Guid DeviceId { get; init; }
    public string ToolUseId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Site { get; init; } = "canvas";
    public string Status { get; init; } = "running";
    public string InputJson { get; init; } = "{}";
    public string? ResultJson { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public static AgentToolCallParams From(AgentStoredToolCall toolCall) => new()
    {
        ToolCallId = toolCall.ToolCallId,
        RunId = toolCall.RunId.Value,
        DeviceId = toolCall.DeviceId.Value,
        ToolUseId = toolCall.ToolUseId,
        ToolName = toolCall.ToolName,
        Site = toolCall.Site,
        Status = toolCall.Status,
        InputJson = toolCall.InputJson,
        ResultJson = toolCall.ResultJson,
        Error = toolCall.Error,
        StartedAt = toolCall.StartedAt,
        FinishedAt = toolCall.FinishedAt,
    };
}

internal sealed class CompleteAgentToolCallParams
{
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public string ToolUseId { get; init; } = string.Empty;
    public string Status { get; init; } = "succeeded";
    public string ResultJson { get; init; } = "{}";
    public string? Error { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
}

internal sealed class InsertAgentEventParams
{
    public Guid RunId { get; init; }
    public Guid DeviceId { get; init; }
    public long Sequence { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AgentSessionRow
{
    public Guid SessionId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid CanvasId { get; init; }
    public string? Title { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public AgentSession ToDomain() => new(
        new AgentSessionId(SessionId), new DeviceId(DeviceId), new CanvasId(CanvasId), Title,
        Utc(CreatedAt), Utc(UpdatedAt));

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal sealed class AgentRunRow
{
    public Guid RunId { get; init; }
    public Guid SessionId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid CanvasId { get; init; }
    public Guid? ProviderId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public string? ExecutorConnectionId { get; init; }
    public int Iteration { get; init; }
    public long LastSeq { get; init; }
    public string? Error { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public AgentRun ToDomain() => new(
        new AgentRunId(RunId), new AgentSessionId(SessionId), new DeviceId(DeviceId),
        new CanvasId(CanvasId), ProviderId is { } provider ? new ProviderId(provider) : null,
        ModelKey, AgentRepository.ParseStatus(Status), ExecutorConnectionId, Iteration, LastSeq,
        Error, InputTokens, OutputTokens, UtcOrNull(StartedAt), UtcOrNull(FinishedAt),
        Utc(CreatedAt), Utc(UpdatedAt));

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? UtcOrNull(DateTime? value) => value is { } actual ? Utc(actual) : null;
}

internal sealed class AgentMessageRow
{
    public Guid MessageId { get; init; }
    public Guid SessionId { get; init; }
    public Guid? RunId { get; init; }
    public Guid DeviceId { get; init; }
    public int Ordinal { get; init; }
    public string Role { get; init; } = "user";
    public string ContentJson { get; init; } = "[]";
    public DateTime CreatedAt { get; init; }

    public AgentStoredMessage ToDomain() => new(
        MessageId, new AgentSessionId(SessionId), RunId is { } run ? new AgentRunId(run) : null,
        new DeviceId(DeviceId), Ordinal, Role, ContentJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}

internal sealed class AgentEventRow
{
    public Guid RunId { get; init; }
    public Guid DeviceId { get; init; }
    public long Seq { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }

    public AgentStoredEvent ToDomain() => new(
        new AgentRunId(RunId), new DeviceId(DeviceId), Seq, Type, PayloadJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}
