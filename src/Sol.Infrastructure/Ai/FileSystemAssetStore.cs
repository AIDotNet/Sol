using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Ai;

/// <summary>
/// Writes generated media under the configured asset root.
/// </summary>
/// <remarks>
/// Files are sharded into <c>yyyy/MM/dd</c> directories. A single flat directory degrades badly
/// once it holds tens of thousands of entries on most filesystems, and dated folders also make
/// "delete everything older than N" a directory removal rather than a scan.
/// <para>
/// Names are random GUIDs, never derived from user input, so a prompt cannot influence the path.
/// <see cref="OpenRead"/> additionally verifies the resolved path stays inside the root, so a
/// tampered database row cannot be used to read arbitrary files.
/// </para>
/// </remarks>
internal sealed class FileSystemAssetStore : IAssetStore
{
    private readonly string _root;
    private readonly ILogger<FileSystemAssetStore> _logger;

    public FileSystemAssetStore(AiOptions options, ILogger<FileSystemAssetStore> logger)
    {
        _root = Path.GetFullPath(options.AssetRoot);
        _logger = logger;

        Directory.CreateDirectory(_root);
    }

    public async Task<StoredAsset> SaveAsync(
        byte[] bytes,
        string mediaType,
        string extensionHint,
        CancellationToken ct)
    {
        var today = DateTimeOffset.UtcNow;
        var relativeDirectory = Path.Combine(
            today.Year.ToString("D4"),
            today.Month.ToString("D2"),
            today.Day.ToString("D2"));

        Directory.CreateDirectory(Path.Combine(_root, relativeDirectory));

        var extension = SanitizeExtension(extensionHint);
        var relativePath = Path.Combine(relativeDirectory, $"{Guid.CreateVersion7():N}{extension}");

        await File.WriteAllBytesAsync(Path.Combine(_root, relativePath), bytes, ct);

        // Stored with forward slashes so a path written on Windows still resolves on Linux.
        return new StoredAsset(relativePath.Replace('\\', '/'), mediaType, bytes.LongLength);
    }

    public Stream? OpenRead(string storagePath)
    {
        if (!TryResolve(storagePath, out var absolute))
        {
            return null;
        }

        try
        {
            return File.OpenRead(absolute);
        }
        catch (FileNotFoundException)
        {
            // The row outlived its file — expected if the asset directory was cleared.
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Delete(string storagePath)
    {
        if (!TryResolve(storagePath, out var absolute))
        {
            return;
        }

        try
        {
            File.Delete(absolute);
        }
        catch (IOException exception)
        {
            // Cleanup is best-effort; a locked file should not fail the caller's operation.
            _logger.LogWarning(exception, "Could not delete asset {Path}", storagePath);
        }
    }

    /// <summary>
    /// Resolves a stored path against the root, rejecting anything that escapes it.
    /// </summary>
    /// <remarks>
    /// Paths come from the database, so this is defence in depth rather than the primary
    /// control — but a row containing <c>../../etc/passwd</c> must not be readable, and the
    /// check costs nothing.
    /// </remarks>
    private bool TryResolve(string storagePath, out string absolute)
    {
        absolute = string.Empty;

        if (string.IsNullOrWhiteSpace(storagePath) || Path.IsPathRooted(storagePath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, storagePath));

        if (!candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected asset path outside the root: {Path}", storagePath);
            return false;
        }

        absolute = candidate;
        return true;
    }

    private static string SanitizeExtension(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return ".bin";

        var extension = hint.StartsWith('.') ? hint : $".{hint}";

        // Only well-formed short alphanumeric extensions; anything else falls back.
        return extension.Length <= 6 && extension[1..].All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : ".bin";
    }
}
