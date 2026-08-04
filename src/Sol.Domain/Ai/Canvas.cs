using Sol.Domain.Identity;

namespace Sol.Domain.Ai;

public readonly record struct CanvasId(Guid Value)
{
    public static CanvasId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out CanvasId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new CanvasId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A saved node graph.
/// </summary>
/// <remarks>
/// <see cref="GraphJson"/> is opaque to the server: it is produced and consumed by the canvas
/// client, and the server only ever stores and returns it. Giving each node a row would buy
/// nothing when the graph is always read and written whole, and would couple the schema to a
/// client-side model that changes far more often.
/// </remarks>
public sealed record Canvas(
    CanvasId Id,
    DeviceId DeviceId,
    string Name,
    string GraphJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
