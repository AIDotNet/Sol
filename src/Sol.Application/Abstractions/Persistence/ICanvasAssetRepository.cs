using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

/// <summary>Metadata for a stored image or video. The bytes themselves live on disk.</summary>
public sealed record CanvasAsset(
    Guid AssetId,
    DeviceId DeviceId,
    string Kind,
    string MediaType,
    string StoragePath,
    long ByteSize,
    string? Prompt,
    DateTimeOffset CreatedAt,
    Guid? GroupId = null);

public interface ICanvasAssetRepository
{
    Task InsertAsync(CanvasAsset asset, CancellationToken ct);

    /// <summary>
    /// Loads asset metadata. Scoped by device, so one caller cannot read another's media by
    /// guessing an id.
    /// </summary>
    Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct);

    /// <summary>
    /// Lists a device's assets, newest first.
    /// </summary>
    /// <remarks>
    /// Capped rather than paged: the library is a picker, not an archive browser, and a device
    /// that has generated thousands of images should not be able to make one request return
    /// all of them.
    /// </remarks>
    Task<IReadOnlyList<CanvasAsset>> ListAsync(
        DeviceId deviceId,
        string? kind,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Removes the metadata row and returns the storage path so the caller can delete the file.
    /// Null when the asset does not exist or belongs to another device.
    /// </summary>
    Task<string?> DeleteAsync(DeviceId deviceId, Guid assetId, CancellationToken ct);

    /// <summary>Lists the device's asset groups with the number of assigned assets.</summary>
    Task<IReadOnlyList<CanvasAssetGroup>> ListGroupsAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>Loads one group when it belongs to the device.</summary>
    Task<CanvasAssetGroup?> FindGroupAsync(DeviceId deviceId, Guid groupId, CancellationToken ct);

    /// <summary>Creates a group, returning null when its name already exists.</summary>
    Task<CanvasAssetGroup?> InsertGroupAsync(CanvasAssetGroup group, CancellationToken ct);

    /// <summary>Renames a group, returning null when it disappeared or the name is taken.</summary>
    Task<CanvasAssetGroup?> RenameGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        string name,
        DateTimeOffset updatedAt,
        CancellationToken ct);

    /// <summary>Deletes a group. The foreign key leaves its assets ungrouped.</summary>
    Task<bool> DeleteGroupAsync(DeviceId deviceId, Guid groupId, CancellationToken ct);

    /// <summary>Assigns or unassigns a batch of assets owned by the device.</summary>
    Task<int> AssignGroupAsync(
        DeviceId deviceId,
        IReadOnlyList<Guid> assetIds,
        Guid? groupId,
        CancellationToken ct);
}
