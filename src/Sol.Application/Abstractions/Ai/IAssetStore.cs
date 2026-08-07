namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Stores generated media in a durable asset backend and hands back a record of where it went.
/// </summary>
/// <remarks>
/// The default cloud backend stores bytes in PostgreSQL. A filesystem fallback remains available
/// only for reading assets created by older local deployments during migration.
/// </remarks>
public interface IAssetStore
{
    /// <param name="extensionHint">
    /// Preferred file extension, derived from the media type. The cloud backend may ignore it;
    /// content is served using the stored media type, not a filename extension.
    /// </param>
    Task<StoredAsset> SaveAsync(
        byte[] bytes,
        string mediaType,
        string extensionHint,
        CancellationToken ct);

    /// <summary>Opens a stored asset for reading, or returns null if the payload is gone.</summary>
    /// <remarks>
    /// A missing file is an expected outcome, not an exception: the database row can outlive the
    /// file if the asset directory was cleared between deploys.
    /// </remarks>
    Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct);

    Task DeleteAsync(string storagePath, CancellationToken ct);
}

/// <param name="StoragePath">An opaque backend key, never a user-controlled filesystem path.</param>
public sealed record StoredAsset(string StoragePath, string MediaType, long ByteSize);
