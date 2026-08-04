namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Stores generated media on disk and hands back a record of where it went.
/// </summary>
/// <remarks>
/// Bytes never go into Postgres. A generated image is a few megabytes and a video far more;
/// the database keeps the metadata and the filesystem keeps the payload.
/// </remarks>
public interface IAssetStore
{
    /// <param name="extensionHint">
    /// Preferred file extension, derived from the media type. Only affects the name on disk —
    /// content is served using the stored media type, not the extension.
    /// </param>
    Task<StoredAsset> SaveAsync(
        byte[] bytes,
        string mediaType,
        string extensionHint,
        CancellationToken ct);

    /// <summary>
    /// Opens a stored asset for reading, or returns null if the file is gone.
    /// </summary>
    /// <remarks>
    /// A missing file is an expected outcome, not an exception: the database row can outlive the
    /// file if the asset directory was cleared between deploys.
    /// </remarks>
    Stream? OpenRead(string storagePath);

    void Delete(string storagePath);
}

/// <param name="StoragePath">Relative to the configured asset root, never an absolute path.</param>
public sealed record StoredAsset(string StoragePath, string MediaType, long ByteSize);
