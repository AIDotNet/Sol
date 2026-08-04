using System.Text.Json.Nodes;

namespace Sol.Application.Contracts.Ai;

/// <param name="Graph">
/// The node graph, passed through verbatim. Typed as <see cref="JsonNode"/> rather than a DTO:
/// the shape is owned by the canvas client and changes with it, and a server-side mirror would
/// have to be kept in lockstep for no gain. <see cref="JsonNode"/> is also AOT-safe, unlike a
/// reflection-bound model.
/// </param>
public sealed record CanvasResponse(
    string Id,
    string Name,
    JsonNode? Graph,
    string CreatedAt,
    string UpdatedAt);

public sealed record CanvasSummaryResponse(
    string Id,
    string Name,
    int NodeCount,
    string CreatedAt,
    string UpdatedAt);

public sealed record CanvasListResponse(CanvasSummaryResponse[] Canvases);

public sealed record CreateCanvasRequest(string? Name, JsonNode? Graph);

/// <param name="Name">Null leaves the current name.</param>
/// <param name="Graph">Null leaves the stored graph — used when only renaming.</param>
public sealed record UpdateCanvasRequest(string? Name, JsonNode? Graph);
