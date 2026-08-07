using System.Text;
using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Skills;

/// <summary>
/// PostgreSQL-backed Skill package store. Uploaded Skill bytes therefore follow the same cloud
/// durability boundary as canvas metadata and generated media. The filesystem implementation is
/// only a compatibility fallback for packages installed before migration 0008.
/// </summary>
internal sealed class CloudSkillStore(
    NpgsqlDataSource dataSource,
    FileSystemSkillStore legacyFiles)
    : ISkillStore
{
    public async Task<string> InstallAsync(
        DeviceId deviceId,
        SkillId skillId,
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken ct)
    {
        if (files.Count == 0) throw new InvalidDataException("The skill package has no files.");

        var storagePath = $"{deviceId.Value:N}/{skillId.Value:N}";
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await connection.ExecuteAsync(
                    """
                    INSERT INTO sol_skill_file
                        (storage_path, relative_path, payload, created_at)
                    VALUES (@StoragePath, @RelativePath, @Payload, @CreatedAt)
                    """,
                    new SkillFileWriteArgs
                    {
                        StoragePath = storagePath,
                        RelativePath = ValidateRelativePath(file.Path),
                        Payload = file.Bytes,
                        CreatedAt = DateTimeOffset.UtcNow,
                    },
                    transaction);
            }

            await transaction.CommitAsync(ct);
            return storagePath;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<string> ReadTextAsync(
        string storagePath,
        string relativePath,
        int maxBytes,
        CancellationToken ct)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var normalizedPath = ValidateRelativePath(relativePath);
        var bytes = await ReadOneAsync(storagePath, normalizedPath, ct);
        if (bytes is null)
        {
            return await legacyFiles.ReadTextAsync(storagePath, normalizedPath, maxBytes, ct);
        }

        if (bytes.LongLength > maxBytes)
        {
            throw new InvalidDataException($"The skill file exceeds {maxBytes} bytes.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The skill file is not valid UTF-8 text.", exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(
        string storagePath,
        int maxEntries,
        CancellationToken ct)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<string>(
            """
            SELECT relative_path
            FROM sol_skill_file
            WHERE storage_path = @StoragePath
            ORDER BY relative_path
            LIMIT @Limit
            """,
            new SkillPathLimitArgs
            {
                StoragePath = ValidateStoragePath(storagePath),
                Limit = checked(maxEntries + 1),
            });
        var paths = rows.ToArray();
        if (paths.Length > maxEntries)
        {
            throw new InvalidDataException("The stored skill has too many files.");
        }
        if (paths.Length > 0) return paths;
        return await legacyFiles.ListFilesAsync(storagePath, maxEntries, ct);
    }

    public async Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(
        string storagePath,
        int maxEntries,
        int maxBytes,
        CancellationToken ct)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SkillFileRow>(
            """
            SELECT relative_path, payload
            FROM sol_skill_file
            WHERE storage_path = @StoragePath
            ORDER BY relative_path
            LIMIT @Limit
            """,
            new SkillPathLimitArgs
            {
                StoragePath = ValidateStoragePath(storagePath),
                Limit = checked(maxEntries + 1),
            });
        var list = rows.ToList();
        if (list.Count > maxEntries)
        {
            throw new InvalidDataException("The stored skill has too many files.");
        }
        if (list.Count == 0)
        {
            return await legacyFiles.ReadPackageAsync(storagePath, maxEntries, maxBytes, ct);
        }

        var total = 0L;
        var files = new List<SkillPackageFile>(list.Count);
        foreach (var row in list)
        {
            total = checked(total + row.Payload.LongLength);
            if (total > maxBytes)
            {
                throw new InvalidDataException("The stored skill exceeds the execution size limit.");
            }

            files.Add(new SkillPackageFile(
                ValidateRelativePath(row.RelativePath),
                row.Payload));
        }

        return files;
    }

    public async Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM sol_skill_file WHERE storage_path = @StoragePath",
            new SkillPathArgs { StoragePath = ValidateStoragePath(storagePath) });
        await legacyFiles.DeleteAsync(storagePath, ct);
    }

    private async Task<byte[]?> ReadOneAsync(
        string storagePath,
        string relativePath,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<byte[]>(
            """
            SELECT payload
            FROM sol_skill_file
            WHERE storage_path = @StoragePath AND relative_path = @RelativePath
            """,
            new SkillFilePathArgs
            {
                StoragePath = ValidateStoragePath(storagePath),
                RelativePath = relativePath,
            });
    }

    private static string ValidateStoragePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2
            && Guid.TryParseExact(segments[0], "N", out _)
            && Guid.TryParseExact(segments[1], "N", out _)
            ? string.Join('/', segments)
            : throw new InvalidDataException("The stored skill path has an invalid shape.");
    }

    private static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/'))
        {
            throw new InvalidDataException($"Invalid relative skill path '{path}'.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." || segment.Length > 255 || segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException($"Unsafe relative skill path '{path}'.");
        }

        var normalized = string.Join('/', segments);
        return normalized.Length <= 512 && Encoding.UTF8.GetByteCount(normalized) <= 1_024
            ? normalized
            : throw new InvalidDataException("A relative Skill path exceeds its length limit.");
    }
}

internal sealed class SkillPathArgs
{
    public string StoragePath { get; init; } = string.Empty;
}

internal sealed class SkillPathLimitArgs
{
    public string StoragePath { get; init; } = string.Empty;
    public int Limit { get; init; }
}

internal sealed class SkillFilePathArgs
{
    public string StoragePath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
}

internal sealed class SkillFileWriteArgs
{
    public string StoragePath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public byte[] Payload { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class SkillFileRow
{
    public string RelativePath { get; init; } = string.Empty;
    public byte[] Payload { get; init; } = [];
}
