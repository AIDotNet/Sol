using Sol.Application.Abstractions.Realtime;
using Sol.Application.Contracts.Agent;
using Sol.Application.Features.Agent;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Ai;

namespace Sol.UnitTests.Ai;

public class AgentCanvasBridgeTests
{
    [Fact]
    public async Task AnAttachedExecutorReceivesAndCompletesOneCall()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run("connection-1");

        var pending = bridge.ExecuteAsync(
            run, "tool-1", "read_canvas", "{}", TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        var sent = await sink.NextAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connection-1", sent.ConnectionId);
        Assert.Equal("tool-1", sent.Payload.ToolUseId);
        Assert.True(bridge.Submit(run.Id, "tool-1", "{\"status\":\"ok\"}", isError: false));

        var result = await pending;
        Assert.False(result.IsError);
        Assert.Equal("{\"status\":\"ok\"}", result.Json);
    }

    [Fact]
    public async Task AttachingAfterAWaitResendsThePendingCall()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run(connectionId: null);

        var pending = bridge.ExecuteAsync(
            run, "tool-2", "create_node", "{\"kind\":\"text\"}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(sink.HasSent);

        await bridge.AttachAsync(run.Id, "connection-2", TestContext.Current.CancellationToken);
        var sent = await sink.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("connection-2", sent.ConnectionId);

        bridge.Submit(run.Id, "tool-2", "{}", isError: false);
        await pending;
    }

    [Fact]
    public async Task ReattachingToTheSameConnectionDoesNotResendThePendingCall()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run(connectionId: null);

        var pending = bridge.ExecuteAsync(
            run, "tool-same-connection", "run_node", "{\"nodeId\":\"video-1\"}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await bridge.AttachAsync(run.Id, "connection-2", TestContext.Current.CancellationToken);
        var sent = await sink.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tool-same-connection", sent.Payload.ToolUseId);

        await bridge.AttachAsync(run.Id, "connection-2", TestContext.Current.CancellationToken);
        Assert.False(sink.HasSent);

        bridge.Submit(run.Id, "tool-same-connection", "{}", isError: false);
        await pending;
    }

    [Fact]
    public async Task DetachThenAttachMovesAnUnfinishedCallWithoutCompletingIt()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run("old-connection");

        var pending = bridge.ExecuteAsync(
            run, "tool-3", "select_nodes", "{\"nodeIds\":[]}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        _ = await sink.NextAsync(TestContext.Current.CancellationToken);

        Assert.Equal([run.Id], bridge.Detach("old-connection"));
        Assert.False(pending.IsCompleted);

        await bridge.AttachAsync(run.Id, "new-connection", TestContext.Current.CancellationToken);
        var resent = await sink.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("new-connection", resent.ConnectionId);
        Assert.Equal("tool-3", resent.Payload.ToolUseId);

        bridge.Submit(run.Id, "tool-3", "{}", isError: false);
        await pending;
    }

    [Fact]
    public async Task AnApprovalCanBeAcceptedByTheAttachedExecutor()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run("connection-approval");

        var pending = bridge.RequestApprovalAsync(
            run, "approval-1", "delete_nodes", "Delete nodes?", "{\"nodeIds\":[\"n1\"]}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var sent = await sink.NextApprovalAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connection-approval", sent.ConnectionId);
        Assert.Equal("delete_nodes", sent.Payload.ToolName);
        Assert.True(bridge.SubmitApproval(run.Id, "approval-1", approved: true));
        Assert.True(await pending);
    }

    [Fact]
    public async Task ACallReturnsAStableErrorWhenNoCanvasArrivesBeforeItsDeadline()
    {
        var bridge = new AgentCanvasBridge(new RecordingSink());
        var result = await bridge.ExecuteAsync(
            Run(connectionId: null), "tool-4", "read_canvas", "{}",
            TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("{\"error\":\"canvas_tool_timeout\"}", result.Json);
    }

    [Fact]
    public void CatalogNamesAreUniqueAndSchemasAreValidJsonObjects()
    {
        Assert.Equal(
            AgentToolCatalog.All.Count,
            AgentToolCatalog.All.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var tool in AgentToolCatalog.All)
        {
            using var document = System.Text.Json.JsonDocument.Parse(tool.InputSchemaJson);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
        }
        Assert.Equal(
            ["load_skill", "run_skill_script"],
            AgentToolCatalog.All
                .Where(tool => tool.Site == AgentToolSite.Server)
                .Select(tool => tool.Name)
                .ToArray());
        Assert.All(
            AgentToolCatalog.All.Where(tool => tool.Site != AgentToolSite.Server),
            tool => Assert.Equal(AgentToolSite.Canvas, tool.Site));

        var approvals = AgentToolCatalog.All
            .Where(tool => tool.Approval == AgentApprovalPolicy.Always)
            .Select(tool => tool.Name)
            .ToArray();
        Assert.Equal(["delete_nodes", "cancel_node", "run_skill_script"], approvals);
    }

    private static AgentRun Run(string? connectionId) => new(
        AgentRunId.New(),
        AgentSessionId.New(),
        DeviceId.New(),
        CanvasId.New(),
        ProviderId.New(),
        "model",
        AgentRunStatus.Running,
        connectionId,
        Iteration: 0,
        LastSequence: 0,
        Error: null,
        InputTokens: null,
        OutputTokens: null,
        StartedAt: DateTimeOffset.UtcNow,
        FinishedAt: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingSink : IAgentRealtimeSink
    {
        private readonly System.Threading.Channels.Channel<SentCall> _calls =
            System.Threading.Channels.Channel.CreateUnbounded<SentCall>();
        private readonly System.Threading.Channels.Channel<SentApproval> _approvals =
            System.Threading.Channels.Channel.CreateUnbounded<SentApproval>();

        public bool HasSent => _calls.Reader.TryPeek(out _);

        public Task SendEventAsync(
            DeviceId deviceId,
            AgentEventEnvelope payload,
            CancellationToken ct) => Task.CompletedTask;

        public Task SendToolCallAsync(
            string connectionId,
            AgentToolCallEnvelope payload,
            CancellationToken ct)
        {
            _calls.Writer.TryWrite(new SentCall(connectionId, payload));
            return Task.CompletedTask;
        }

        public Task SendApprovalRequestAsync(
            string connectionId,
            AgentApprovalEnvelope payload,
            CancellationToken ct)
        {
            _approvals.Writer.TryWrite(new SentApproval(connectionId, payload));
            return Task.CompletedTask;
        }

        public ValueTask<SentCall> NextAsync(CancellationToken ct) => _calls.Reader.ReadAsync(ct);
        public ValueTask<SentApproval> NextApprovalAsync(CancellationToken ct) =>
            _approvals.Reader.ReadAsync(ct);
    }

    private sealed record SentCall(string ConnectionId, AgentToolCallEnvelope Payload);
    private sealed record SentApproval(string ConnectionId, AgentApprovalEnvelope Payload);
}
