using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Sol.Application.Abstractions.Caching;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Realtime;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Api.Hubs;

/// <summary>
/// The application's realtime hub.
/// </summary>
/// <remarks>
/// Derives from the plain <see cref="Hub"/>, never <c>Hub&lt;TClient&gt;</c>. Strongly typed
/// hubs build their client proxy with <c>Reflection.Emit</c> (see
/// <c>TypedClientBuilder&lt;T&gt;</c>, annotated <c>RequiresDynamicCode</c>), which warns at
/// build time and throws at runtime under Native AOT. Client methods are therefore addressed by
/// name — the names live in <see cref="RealtimeMethods"/> so they are not scattered as literals.
/// <para>
/// Other AOT constraints on hub methods: only the JSON protocol is supported (MessagePack is
/// not); return types are limited to Task/Task&lt;T&gt;/ValueTask/ValueTask&lt;T&gt;; and
/// <c>IAsyncEnumerable&lt;T&gt;</c> or <c>ChannelReader&lt;T&gt;</c> parameters whose T is a
/// struct fail at application startup.
/// </para>
/// </remarks>
public sealed class SolHub(
    IPresenceTracker presence,
    IAgentRepository agents,
    IAgentCanvasBridge canvasBridge,
    ILogger<SolHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var context = Context.GetHttpContext();
        var deviceId = context is not null ? DeviceCookie.Read(context) : null;

        // Without an identity there is nothing to route messages to, and no way to attribute
        // activity. Refuse the connection so the client performs the handshake first.
        if (deviceId is not { } device)
        {
            logger.LogDebug("Rejecting connection {ConnectionId}: no device cookie.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ForDevice(device));

        if (context?.Items[DeviceContextItems.VisitorId] is VisitorId visitor)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ForVisitor(visitor));
        }

        await presence.MarkOnlineAsync(device, Context.ConnectionId, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var context = Context.GetHttpContext();
        if (context is not null && DeviceCookie.Read(context) is { } device)
        {
            // CancellationToken.None: the connection token is already cancelled here, and the
            // presence and executor entries must still be cleaned up.
            await presence.MarkOfflineAsync(device, Context.ConnectionId, CancellationToken.None);
            _ = canvasBridge.Detach(Context.ConnectionId);
            await agents.ClearExecutorConnectionAsync(
                device, Context.ConnectionId, CancellationToken.None);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<string> AttachAgentRun(string runId, string canvasId)
    {
        if (ReadDevice() is not { } device
            || !AgentRunId.TryParse(runId, out var parsedRun)
            || !CanvasId.TryParse(canvasId, out var parsedCanvas))
        {
            throw new HubException("Agent run not found.");
        }

        var claimed = await agents.ClaimExecutorConnectionAsync(
            device, parsedRun, parsedCanvas, Context.ConnectionId, Context.ConnectionAborted);
        if (!claimed)
        {
            throw new HubException("Agent run not found.");
        }

        await canvasBridge.AttachAsync(parsedRun, Context.ConnectionId, Context.ConnectionAborted);
        return Context.ConnectionId;
    }

    public async Task DetachAgentRun(string runId)
    {
        if (ReadDevice() is not { } device || !AgentRunId.TryParse(runId, out var parsedRun))
        {
            return;
        }

        var run = await agents.FindRunAsync(device, parsedRun, Context.ConnectionAborted);
        if (run is null
            || !string.Equals(run.ExecutorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            return;
        }

        _ = canvasBridge.Detach(Context.ConnectionId);
        await agents.ClearExecutorConnectionAsync(device, Context.ConnectionId, Context.ConnectionAborted);
    }

    public async Task SubmitToolResult(
        string runId,
        string toolUseId,
        string resultJson,
        bool isError)
    {
        if (ReadDevice() is not { } device
            || !AgentRunId.TryParse(runId, out var parsedRun)
            || string.IsNullOrWhiteSpace(toolUseId)
            || resultJson.Length > 1_000_000)
        {
            throw new HubException("Agent tool call not found.");
        }

        try
        {
            using var _ = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            throw new HubException("Agent tool result must be valid JSON.");
        }

        var run = await agents.FindRunAsync(device, parsedRun, Context.ConnectionAborted);
        if (run is null
            || !string.Equals(run.ExecutorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            throw new HubException("Agent tool call not found.");
        }

        _ = canvasBridge.Submit(parsedRun, toolUseId, resultJson, isError);
    }

    public async Task SubmitApproval(string runId, string approvalId, bool approved)
    {
        if (ReadDevice() is not { } device
            || !AgentRunId.TryParse(runId, out var parsedRun)
            || string.IsNullOrWhiteSpace(approvalId))
        {
            throw new HubException("Agent approval not found.");
        }

        var run = await agents.FindRunAsync(device, parsedRun, Context.ConnectionAborted);
        if (run is null
            || !string.Equals(run.ExecutorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            throw new HubException("Agent approval not found.");
        }

        _ = canvasBridge.SubmitApproval(parsedRun, approvalId, approved);
    }

    /// <summary>Round-trip check for clients verifying the transport is live.</summary>
    public Task<string> Ping() => Task.FromResult("pong");

    private DeviceId? ReadDevice()
    {
        var context = Context.GetHttpContext();
        return context is null ? null : DeviceCookie.Read(context);
    }

}
