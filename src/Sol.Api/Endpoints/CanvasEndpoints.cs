using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;

namespace Sol.Api.Endpoints;

/// <summary>
/// Canvas documents, scoped to the calling device.
/// </summary>
/// <remarks>
/// The client keeps its own localStorage copy for instant load and offline editing; this is the
/// durable one. Without it a canvas lived only in the browser, so clearing site data destroyed
/// every graph the user had made.
/// </remarks>
public static class CanvasEndpoints
{
    /// <summary>
    /// Ceiling on a stored graph. Generous — a graph is text, and images live in the asset
    /// store, not inline — but an unbounded jsonb column is a denial-of-service vector.
    /// </summary>
    private const int MaxGraphBytes = 8 * 1024 * 1024;

    private const int MaxNameLength = 200;

    /// <summary>
    /// What a canvas with no graph is stored as.
    /// </summary>
    /// <remarks>
    /// A well-formed empty graph rather than <c>{}</c>: clients destructure `nodes` and `edges`
    /// directly, and an object missing them reads as truthy while yielding undefined arrays.
    /// </remarks>
    private const string EmptyGraph = """{"nodes":[],"edges":[]}""";

    public static IEndpointRouteBuilder MapCanvasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/canvas").WithTags("canvas");

        group.MapGet("/", ListAsync).WithName("ListCanvases");
        group.MapPost("/", CreateAsync).WithName("CreateCanvas");
        group.MapGet("/{id}", GetAsync).WithName("GetCanvas");
        group.MapPut("/{id}", UpdateAsync).WithName("UpdateCanvas");
        group.MapDelete("/{id}", DeleteAsync).WithName("DeleteCanvas");

        return app;
    }

    private static async Task<Results<Ok<CanvasListResponse>, UnauthorizedHttpResult>> ListAsync(
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        var list = await canvases.ListAsync(deviceId, ct);

        return TypedResults.Ok(new CanvasListResponse(
        [
            .. list.Select(summary => new CanvasSummaryResponse(
                summary.CanvasId.ToString(),
                summary.Name,
                summary.NodeCount,
                summary.CreatedAt.ToString("O"),
                summary.UpdatedAt.ToString("O"))),
        ]));
    }

    private static async Task<Results<Created<CanvasResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> CreateAsync(
        CreateCanvasRequest request,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!TryReadGraph(request.Graph, out var graphJson, out var error))
        {
            return Invalid(error);
        }

        var now = DateTimeOffset.UtcNow;
        var canvas = new Canvas(
            CanvasId.New(),
            deviceId,
            NormalizeName(request.Name),
            graphJson,
            now,
            now);

        await canvases.InsertAsync(canvas, ct);

        return TypedResults.Created($"/api/v1/canvas/{canvas.Id}", ToResponse(canvas));
    }

    private static async Task<Results<Ok<CanvasResponse>, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> GetAsync(
        string id,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!CanvasId.TryParse(id, out var canvasId))
        {
            return Invalid("malformed canvas id");
        }

        var canvas = await canvases.FindAsync(deviceId, canvasId, ct);

        return canvas is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(canvas));
    }

    /// <summary>
    /// Replaces a canvas. Creates it at the supplied id when absent, so the client can adopt a
    /// canvas it already had locally without a separate round trip.
    /// </summary>
    private static async Task<Results<Ok<CanvasResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> UpdateAsync(
        string id,
        UpdateCanvasRequest request,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!CanvasId.TryParse(id, out var canvasId))
        {
            return Invalid("malformed canvas id");
        }

        var existing = await canvases.FindAsync(deviceId, canvasId, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            if (!TryReadGraph(request.Graph, out var newGraph, out var createError))
            {
                return Invalid(createError);
            }

            var created = new Canvas(
                canvasId, deviceId, NormalizeName(request.Name), newGraph, now, now);

            await canvases.InsertAsync(created, ct);
            return TypedResults.Ok(ToResponse(created));
        }

        // A null graph means "rename only", so a rename cannot accidentally blank the document.
        var graphJson = existing.GraphJson;
        if (request.Graph is not null)
        {
            if (!TryReadGraph(request.Graph, out graphJson, out var updateError))
            {
                return Invalid(updateError);
            }
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? existing.Name
                : Truncate(request.Name.Trim()),
            GraphJson = graphJson,
            UpdatedAt = now,
        };

        await canvases.UpdateAsync(updated, ct);

        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> DeleteAsync(
        string id,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!CanvasId.TryParse(id, out var canvasId))
        {
            return Invalid("malformed canvas id");
        }

        return await canvases.DeleteAsync(deviceId, canvasId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    /// <summary>
    /// Serializes the incoming graph and enforces the size cap.
    /// </summary>
    /// <remarks>
    /// The body is checked after serialization rather than by trusting Content-Length, because
    /// the request may be compressed and the stored size is what the cap is protecting.
    /// </remarks>
    private static bool TryReadGraph(JsonNode? graph, out string json, out string error)
    {
        json = graph?.ToJsonString() ?? EmptyGraph;

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxGraphBytes)
        {
            error = $"the graph exceeds the {MaxGraphBytes / (1024 * 1024)} MB limit";
            json = EmptyGraph;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "未命名画布" : Truncate(name.Trim());

    private static string Truncate(string value) =>
        value.Length <= MaxNameLength ? value : value[..MaxNameLength];

    private static CanvasResponse ToResponse(Canvas canvas)
    {
        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(canvas.GraphJson);
        }
        catch (JsonException)
        {
            // Stored jsonb is always valid JSON, so this is unreachable in practice — but
            // returning null beats throwing a 500 over a document the user could otherwise
            // overwrite from their local copy.
            graph = null;
        }

        return new CanvasResponse(
            canvas.Id.ToString(),
            canvas.Name,
            graph,
            canvas.CreatedAt.ToString("O"),
            canvas.UpdatedAt.ToString("O"));
    }

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
