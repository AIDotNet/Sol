using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

/// <summary>
/// Canvas storage. Every method is scoped by <see cref="DeviceId"/>, so ownership is part of the
/// query rather than a check a caller can forget.
/// </summary>
public interface ICanvasRepository
{
    /// <summary>
    /// Lists a device's canvases, most recently updated first. The graph body is omitted —
    /// a switcher only needs names, and the graphs can be megabytes each.
    /// </summary>
    Task<IReadOnlyList<CanvasSummary>> ListAsync(DeviceId deviceId, CancellationToken ct);

    Task<Canvas?> FindAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct);

    Task InsertAsync(Canvas canvas, CancellationToken ct);

    /// <summary>
    /// Writes the graph and name. Returns false when no row matched, which means the canvas does
    /// not exist or belongs to another device.
    /// </summary>
    Task<bool> UpdateAsync(Canvas canvas, CancellationToken ct);

    Task<bool> DeleteAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct);
}

/// <param name="NodeCount">
/// Computed in SQL from the stored graph, so listing never ships or parses a graph body.
/// </param>
public sealed record CanvasSummary(
    Guid CanvasId,
    string Name,
    int NodeCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
