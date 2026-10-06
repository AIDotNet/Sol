using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Contracts.Agent;
using Sol.Application.Features.Agent;
using Sol.Domain.Ai;

namespace Sol.Api.Endpoints;

public static class AgentEndpoints
{
    private const int MaxPromptLength = 100_000;
    private const int MaxTitleLength = 200;
    private const int MaxAgentImages = 8;
    private const int MaxCanvasContextLength = 24_000;

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agent").WithTags("agent");

        group.MapPost("/sessions", CreateSessionAsync).WithName("CreateAgentSession");
        group.MapGet("/sessions", GetSessionByCanvasAsync).WithName("GetAgentSessionByCanvas");
        group.MapGet("/sessions/{id}/messages", ListMessagesAsync).WithName("ListAgentMessages");
        group.MapDelete("/sessions/{id}/messages", ClearMessagesAsync).WithName("ClearAgentMessages");
        group.MapPost("/sessions/{id}/runs", CreateRunAsync).WithName("CreateAgentRun");
        group.MapGet("/runs/active", GetActiveRunAsync).WithName("GetActiveAgentRun");
        group.MapGet("/runs/{id}", GetRunAsync).WithName("GetAgentRun");
        group.MapGet("/runs/{id}/events", ListEventsAsync).WithName("ListAgentEvents");
        group.MapDelete("/runs/{id}", CancelRunAsync).WithName("CancelAgentRun");
        group.MapGet("/tools", ListTools).WithName("ListAgentTools");

        return app;
    }

    private static async Task<Results<Ok<AgentSessionResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> CreateSessionAsync(
        CreateAgentSessionRequest request,
        HttpContext http,
        ICanvasRepository canvases,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!CanvasId.TryParse(request.CanvasId, out var canvasId)) return Invalid("malformed canvas id");
        if (await canvases.FindAsync(deviceId, canvasId, ct) is null) return TypedResults.NotFound();

        if (await agents.FindSessionByCanvasAsync(deviceId, canvasId, ct) is { } existing)
        {
            return TypedResults.Ok(ToResponse(existing));
        }

        var now = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? null
            : request.Title.Trim()[..Math.Min(request.Title.Trim().Length, MaxTitleLength)];
        var session = new AgentSession(
            AgentSessionId.New(), deviceId, canvasId, title, now, now);
        await agents.InsertSessionAsync(session, ct);
        return TypedResults.Ok(ToResponse(session));
    }

    private static async Task<Results<Ok<AgentSessionResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> GetSessionByCanvasAsync(
        string canvasId,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!CanvasId.TryParse(canvasId, out var parsed)) return Invalid("malformed canvas id");
        var session = await agents.FindSessionByCanvasAsync(deviceId, parsed, ct);
        return session is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(session));
    }

    private static async Task<Results<Ok<AgentMessageListResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> ListMessagesAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!AgentSessionId.TryParse(id, out var sessionId)) return Invalid("malformed session id");
        if (await agents.FindSessionAsync(deviceId, sessionId, ct) is null) return TypedResults.NotFound();

        var messages = await agents.ListMessagesAsync(deviceId, sessionId, ct);
        return TypedResults.Ok(new AgentMessageListResponse(
        [
            .. messages.Select(message => new AgentMessageResponse(
                message.MessageId.ToString(), message.RunId?.ToString(), message.Ordinal,
                message.Role, message.ContentJson, message.CreatedAt.ToString("O"))),
        ]));
    }

    private static async Task<Results<NoContent, BadRequest<ErrorResponse>, NotFound,
        UnauthorizedHttpResult>> ClearMessagesAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!AgentSessionId.TryParse(id, out var sessionId)) return Invalid("malformed session id");

        var session = await agents.FindSessionAsync(deviceId, sessionId, ct);
        if (session is null) return TypedResults.NotFound();
        if (await agents.FindActiveRunAsync(deviceId, session.CanvasId, ct) is not null)
        {
            return Invalid("stop the active Agent run before clearing its messages");
        }

        await agents.ClearMessagesAsync(deviceId, sessionId, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<AgentRunResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> CreateRunAsync(
        string id,
        CreateAgentRunRequest request,
        HttpContext http,
        IAgentRepository agents,
        IProviderRepository providers,
        IAgentRunControl control,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!AgentSessionId.TryParse(id, out var sessionId)) return Invalid("malformed session id");
        var session = await agents.FindSessionAsync(deviceId, sessionId, ct);
        if (session is null) return TypedResults.NotFound();
        if (!ProviderId.TryParse(request.ProviderId, out var providerId)) return Invalid("malformed provider id");
        if (string.IsNullOrWhiteSpace(request.Prompt)) return Invalid("a prompt is required");
        if (request.Prompt.Length > MaxPromptLength) return Invalid("the prompt is too long");
        if (request.ExecutorConnectionId is { Length: > 200 }) return Invalid("the connection id is too long");
        if (request.CanvasContext is { Length: > MaxCanvasContextLength })
        {
            return Invalid("the canvas context is too long");
        }

        if (await agents.FindActiveRunAsync(deviceId, session.CanvasId, ct) is not null)
        {
            return Invalid("this canvas already has an active Agent run");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        var model = provider?.Models.FirstOrDefault(candidate =>
            candidate.ModelKey == request.ModelKey && candidate.Category == ModelCategory.Chat);
        if (provider is null) return TypedResults.NotFound();
        if (!provider.Enabled || model is null || !model.Enabled)
        {
            return Invalid("the selected Agent model is unavailable or disabled");
        }
        var protocol = provider.ResolveProtocol(model);
        if (protocol is not (ProviderType.Anthropic
            or ProviderType.OpenAiChat
            or ProviderType.OpenAiResponses))
        {
            return Invalid($"protocol '{protocol.ToWire()}' does not support Agent runs yet");
        }

        var imageUrls = request.Images ?? [];
        if (imageUrls.Length > MaxAgentImages)
        {
            return Invalid($"at most {MaxAgentImages} images are allowed per Agent run");
        }

        if (imageUrls.Length > 0 && !model.SupportsVision)
        {
            return Invalid("the selected Agent model does not support image input");
        }

        var imageAssets = new List<CanvasAsset>(imageUrls.Length);
        foreach (var imageUrl in imageUrls)
        {
            if (!TryParseAssetUrl(imageUrl, out var assetId))
            {
                return Invalid("Agent images must be uploaded canvas assets");
            }

            var asset = await assets.FindAsync(deviceId, assetId, ct);
            if (asset is null || asset.Kind != "image")
            {
                return Invalid("one of the Agent images is no longer available");
            }

            imageAssets.Add(asset);
        }

        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun(
            AgentRunId.New(), session.Id, deviceId, session.CanvasId, provider.Id,
            request.ModelKey, AgentRunStatus.Queued, ExecutorConnectionId: null, 0, 0,
            null, null, null, null, null, now, now,
            string.IsNullOrWhiteSpace(request.CanvasContext) ? null : request.CanvasContext);
        await agents.InsertRunAsync(run, ct);

        var ordinal = await agents.NextMessageOrdinalAsync(deviceId, session.Id, ct);
        var content = new JsonArray
        {
            (JsonNode)new JsonObject { ["kind"] = "text", ["text"] = request.Prompt },
        };
        for (var index = 0; index < imageAssets.Count; index++)
        {
            content.Add((JsonNode)new JsonObject
            {
                ["kind"] = "image",
                ["imageUrl"] = imageUrls[index],
                ["mediaType"] = imageAssets[index].MediaType,
            });
        }
        await agents.InsertMessageAsync(
            new AgentStoredMessage(
                Guid.CreateVersion7(), session.Id, run.Id, deviceId, ordinal, "user",
                content.ToJsonString(), now),
            ct);

        control.Enqueue(run.Id);
        return TypedResults.Ok(ToResponse(run));
    }

    private static async Task<Results<Ok<AgentRunResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> GetActiveRunAsync(
        string canvasId,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!CanvasId.TryParse(canvasId, out var parsed)) return Invalid("malformed canvas id");
        var run = await agents.FindActiveRunAsync(deviceId, parsed, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(run));
    }

    private static async Task<Results<Ok<AgentRunResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> GetRunAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!AgentRunId.TryParse(id, out var runId)) return Invalid("malformed run id");
        var run = await agents.FindRunAsync(deviceId, runId, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(run));
    }

    private static async Task<Results<Ok<AgentEventListResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> ListEventsAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct,
        long? afterSeq = null)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!AgentRunId.TryParse(id, out var runId)) return Invalid("malformed run id");
        if (await agents.FindRunAsync(deviceId, runId, ct) is null) return TypedResults.NotFound();
        var events = await agents.ListEventsAsync(deviceId, runId, afterSeq ?? 0, ct);
        return TypedResults.Ok(new AgentEventListResponse(
        [
            .. events.Select(item => new AgentEventResponse(
                item.RunId.ToString(), item.Sequence, item.Type, item.PayloadJson,
                item.CreatedAt.ToString("O"))),
        ]));
    }

    private static async Task<Results<NoContent, BadRequest<ErrorResponse>, NotFound,
        UnauthorizedHttpResult>> CancelRunAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        IAgentRunControl control,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!AgentRunId.TryParse(id, out var runId)) return Invalid("malformed run id");
        var run = await agents.FindRunAsync(deviceId, runId, ct);
        if (run is null) return TypedResults.NotFound();
        if (run.Status is AgentRunStatus.Succeeded or AgentRunStatus.Failed
            or AgentRunStatus.Cancelled or AgentRunStatus.Interrupted)
        {
            return TypedResults.NoContent();
        }

        control.Cancel(runId);
        var now = DateTimeOffset.UtcNow;
        await agents.UpdateRunAsync(run with
        {
            Status = AgentRunStatus.Cancelled,
            Error = null,
            FinishedAt = now,
            UpdatedAt = now,
        }, ct);
        return TypedResults.NoContent();
    }

    private static Results<Ok<AgentToolCatalogResponse>, UnauthorizedHttpResult> ListTools(
        HttpContext http)
    {
        if (http.GetDeviceId() is null) return TypedResults.Unauthorized();
        return TypedResults.Ok(new AgentToolCatalogResponse(
        [
            .. AgentToolCatalog.All.Select(tool => new AgentToolCatalogEntryResponse(
                tool.Name,
                tool.Site == AgentToolSite.Canvas ? "canvas" : "server",
                tool.Approval switch
                {
                    AgentApprovalPolicy.Never => "never",
                    AgentApprovalPolicy.Always => "always",
                    AgentApprovalPolicy.DestructiveAction => "destructive_action",
                    _ => throw new ArgumentOutOfRangeException(),
                },
                tool.Description,
                tool.InputSchemaJson,
                tool.TimeoutMilliseconds)),
        ]));
    }

    private static AgentSessionResponse ToResponse(AgentSession session) => new(
        session.Id.ToString(), session.CanvasId.ToString(), session.Title,
        session.CreatedAt.ToString("O"), session.UpdatedAt.ToString("O"));

    private static AgentRunResponse ToResponse(AgentRun run) => new(
        run.Id.ToString(), run.SessionId.ToString(), run.CanvasId.ToString(),
        run.ProviderId?.ToString(), run.ModelKey, AgentRepositoryStatus(run.Status), run.Iteration,
        run.LastSequence, run.Error, run.CreatedAt.ToString("O"), run.UpdatedAt.ToString("O"));

    private static bool TryParseAssetUrl(string? url, out Guid assetId)
    {
        const string prefix = "/api/v1/canvas/assets/";
        assetId = default;
        if (url is null || !url.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var idSegment = url[prefix.Length..].Split('?')[0];
        return Guid.TryParse(idSegment, out assetId) && assetId != Guid.Empty;
    }

    private static string AgentRepositoryStatus(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Queued => "queued",
        AgentRunStatus.Running => "running",
        AgentRunStatus.AwaitingCanvas => "awaiting_canvas",
        AgentRunStatus.AwaitingApproval => "awaiting_approval",
        AgentRunStatus.Succeeded => "succeeded",
        AgentRunStatus.Failed => "failed",
        AgentRunStatus.Cancelled => "cancelled",
        AgentRunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
