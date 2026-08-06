using Sol.Domain.Identity;

namespace Sol.Domain.Ai;

/// <summary>
/// A user-created folder for the media in a device's canvas asset library.
/// </summary>
/// <remarks>
/// Groups are deliberately scoped to a device, just like assets. Deleting a group only removes
/// the assignment from its assets; it never deletes the media itself.
/// </remarks>
public sealed record CanvasAssetGroup(
    Guid GroupId,
    DeviceId DeviceId,
    string Name,
    int AssetCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
