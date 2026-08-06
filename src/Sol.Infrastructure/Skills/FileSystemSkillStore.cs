using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Skills;

internal sealed class FileSystemSkillStore : ISkillStore
{
    private readonly string root;

    public FileSystemSkillStore(IOptions<SkillsOptions> configuredOptions)
    {
        root = Path.GetFullPath(configuredOptions.Value.Root);
        SecureCreateDirectory(root);
        RejectPathItself(root);
    }

    public async Task<string> InstallAsync(
        DeviceId deviceId,
        SkillId skillId,
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken ct)
    {
        if (files.Count == 0) throw new InvalidDataException("The skill package has no files.");

        var deviceRoot = ContainedPath(root, deviceId.Value.ToString("N"));
        var finalPath = ContainedPath(deviceRoot, skillId.Value.ToString("N"));
        var stagingPath = ContainedPath(deviceRoot, $".{skillId.Value:N}.installing");
        RejectPathItself(root);
        SecureCreateDirectory(deviceRoot);
        RejectReparsePoints(root, deviceRoot);
        if (Directory.Exists(finalPath) || Directory.Exists(stagingPath)
            || File.Exists(finalPath) || File.Exists(stagingPath))
        {
            throw new IOException("The skill storage directory already exists.");
        }

        SecureCreateDirectory(stagingPath);
        RejectReparsePoints(root, stagingPath);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = ValidateRelativePath(file.Path);
                var targetPath = ContainedPath(stagingPath, relativePath);
                var parent = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidDataException($"Invalid skill file path '{file.Path}'.");
                SecureCreateDirectory(parent);
                RejectReparsePoints(stagingPath, parent);
                RejectPathItself(targetPath);
                await using var output = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await output.WriteAsync(file.Bytes, ct);
                await output.FlushAsync(ct);
            }

            Directory.Move(stagingPath, finalPath);
            return Path.GetRelativePath(root, finalPath).Replace('\\', '/');
        }
        catch
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
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
        var installPath = ResolveStoragePath(storagePath);
        var filePath = ContainedPath(installPath, ValidateRelativePath(relativePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("Skill file not found.");
        RejectReparsePoints(installPath, filePath);

        var info = new FileInfo(filePath);
        if (info.Length > maxBytes)
        {
            throw new InvalidDataException($"The skill file exceeds {maxBytes} bytes.");
        }

        var bytes = new byte[(int)info.Length];
        await using var input = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException("The skill file ended unexpectedly.");
            offset += read;
        }
        if (await input.ReadAsync(new byte[1], ct) != 0)
        {
            throw new InvalidDataException($"The skill file exceeds {maxBytes} bytes.");
        }

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new InvalidDataException("The skill file is not valid UTF-8 text.", exception);
        }
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(
        string storagePath,
        int maxEntries,
        CancellationToken ct)
    {
        var (installPath, paths) = ListSafePaths(storagePath, maxEntries, ct);
        return Task.FromResult<IReadOnlyList<string>>(
            [.. paths.Select(path => Path.GetRelativePath(installPath, path).Replace('\\', '/'))]);
    }

    public async Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(
        string storagePath,
        int maxEntries,
        int maxBytes,
        CancellationToken ct)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var (installPath, paths) = ListSafePaths(storagePath, maxEntries, ct);
        var files = new List<SkillPackageFile>(paths.Length);
        long total = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            RejectReparsePoints(installPath, path);
            var info = new FileInfo(path);
            total = checked(total + info.Length);
            if (total > maxBytes || info.Length > int.MaxValue)
            {
                throw new InvalidDataException("The stored skill exceeds the execution size limit.");
            }
            var bytes = new byte[(int)info.Length];
            await using var input = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.ReadExactlyAsync(bytes, ct);
            var relative = Path.GetRelativePath(installPath, path).Replace('\\', '/');
            files.Add(new SkillPackageFile(ValidateRelativePath(relative), bytes));
        }
        return files;
    }

    private (string InstallPath, string[] Paths) ListSafePaths(
        string storagePath,
        int maxEntries,
        CancellationToken ct)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        ct.ThrowIfCancellationRequested();
        var installPath = ResolveStoragePath(storagePath);
        if (!Directory.Exists(installPath)) throw new DirectoryNotFoundException("Skill storage is missing.");
        RejectReparsePoints(root, installPath);
        var paths = new List<string>(Math.Min(maxEntries, 128));
        foreach (var path in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (paths.Count >= maxEntries)
            {
                throw new InvalidDataException("The stored skill has too many files.");
            }
            RejectReparsePoints(installPath, path);
            paths.Add(path);
        }
        return (installPath, [.. paths]);
    }

    public Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var installPath = ResolveStoragePath(storagePath);
        if (!Directory.Exists(installPath)) return Task.CompletedTask;
        RejectReparsePoints(root, installPath);
        Directory.Delete(installPath, recursive: true);
        return Task.CompletedTask;
    }

    private string ResolveStoragePath(string storagePath)
    {
        var relative = ValidateRelativePath(storagePath);
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out _)
            || !Guid.TryParseExact(parts[1], "N", out _))
        {
            throw new InvalidDataException("The stored skill path has an invalid shape.");
        }
        return ContainedPath(root, relative);
    }

    private static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\')
            || path.StartsWith('/') || Path.IsPathRooted(path))
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
        if (normalized.Length > 512
            || System.Text.Encoding.UTF8.GetByteCount(normalized) > 1_024)
        {
            throw new InvalidDataException("A relative Skill path exceeds its length limit.");
        }
        return normalized;
    }

    private static string ContainedPath(string parent, string relativePath)
    {
        var parentFull = Path.GetFullPath(parent);
        var candidate = Path.GetFullPath(Path.Combine(parentFull, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(parentFull) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The skill path escapes its storage directory.");
        }
        return candidate;
    }

    private static void SecureCreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RejectReparsePoints(string parent, string candidate)
    {
        RejectPathItself(parent);
        var relative = Path.GetRelativePath(parent, candidate);
        var current = Path.GetFullPath(parent);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectPathItself(current);
        }
    }

    private static void RejectPathItself(string path)
    {
        var info = Directory.Exists(path)
            ? (FileSystemInfo)new DirectoryInfo(path)
            : new FileInfo(path);
        try
        {
            // FileSystemInfo.Attributes is -1 for a missing path on Unix, and -1 contains every
            // bit including ReparsePoint. Only inspect attributes when the entry really exists;
            // LinkTarget still catches a broken symlink whose target does not.
            if (info.LinkTarget is not null
                || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("Symbolic links are not allowed in skill storage.");
            }
        }
        catch (FileNotFoundException)
        {
            // A path that does not exist yet is safe to create under already-validated parents.
        }
    }
}
