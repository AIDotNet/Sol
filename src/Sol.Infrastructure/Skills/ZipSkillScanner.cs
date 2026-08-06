using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Skills;

internal sealed partial class ZipSkillScanner(IOptions<SkillsOptions> configuredOptions)
    : ISkillPackageScanner
{
    private const string ManifestName = "SKILL.md";
    private const int MaxFrontmatterBytes = 64 * 1024;
    private const int MaxCompressionRatio = 200;
    private const int MaxRiskScanBytes = 2 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".py", ".js",
    };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".tgz", ".gz", ".bz2", ".xz", ".7z", ".rar",
    };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".wasm", ".class", ".jar",
    };

    private readonly SkillsOptions options = configuredOptions.Value;

    public async Task<ScannedSkillPackage> ScanAsync(
        Stream stream,
        string fileName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new InvalidDataException("The skill package is not readable.");

        var upload = await ReadBoundedAsync(stream, options.MaxUploadBytes, ct);
        IReadOnlyList<SkillPackageFile> files;

        if (string.Equals(Path.GetFileName(fileName), ManifestName, StringComparison.OrdinalIgnoreCase))
        {
            files = [new SkillPackageFile(ManifestName, upload)];
        }
        else if (string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            files = ReadZip(upload);
        }
        else
        {
            throw new InvalidDataException("Upload SKILL.md or a .zip skill package.");
        }

        var manifest = files.SingleOrDefault(file =>
            string.Equals(file.Path, ManifestName, StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
        {
            throw new InvalidDataException("The package must contain a root SKILL.md file.");
        }
        if (manifest.Bytes.Length > MaxFrontmatterBytes + options.MaxExtractedBytes)
        {
            throw new InvalidDataException("SKILL.md exceeds the package size limit.");
        }

        string markdown;
        try
        {
            markdown = StrictUtf8.GetString(manifest.Bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SKILL.md must be valid UTF-8 text.", exception);
        }

        var metadata = ParseManifest(markdown);
        var risks = ScanRisks(files);
        var maxRisk = risks.Count == 0
            ? SkillRiskLevel.Safe
            : risks.Max(finding => finding.Severity);

        return new ScannedSkillPackage(
            metadata.Name,
            Slugify(metadata.Name),
            metadata.Description,
            metadata.Instructions,
            files,
            risks,
            files.Any(file => ScriptExtensions.Contains(Path.GetExtension(file.Path))),
            maxRisk,
            files.Sum(file => (long)file.Bytes.Length));
    }

    private IReadOnlyList<SkillPackageFile> ReadZip(byte[] upload)
    {
        try
        {
            using var input = new MemoryStream(upload, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0) throw new InvalidDataException("The skill package is empty.");
            if (archive.Entries.Count > options.MaxEntries)
            {
                throw new InvalidDataException(
                    $"The skill package contains more than {options.MaxEntries} entries.");
            }

            var candidates = new List<ArchiveFile>(archive.Entries.Count);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedBytes = 0;

            foreach (var entry in archive.Entries)
            {
                var path = ValidateArchivePath(entry.FullName);
                if (IsSymlink(entry))
                {
                    throw new InvalidDataException($"Symbolic links are not allowed: '{path}'.");
                }
                if (entry.Name.Length == 0) continue;
                if (!names.Add(path))
                {
                    throw new InvalidDataException($"The package contains a duplicate path: '{path}'.");
                }
                if (ArchiveExtensions.Contains(Path.GetExtension(path)))
                {
                    throw new InvalidDataException($"Nested archives are not allowed: '{path}'.");
                }
                if (entry.Length < 0 || entry.CompressedLength < 0)
                {
                    throw new InvalidDataException($"The package contains an invalid entry: '{path}'.");
                }
                if (entry.Length > options.MaxExtractedBytes)
                {
                    throw new InvalidDataException($"The file '{path}' exceeds the extraction limit.");
                }
                if (entry.Length > 0 && entry.CompressedLength == 0)
                {
                    throw new InvalidDataException($"The file '{path}' has an invalid compressed size.");
                }
                if (entry.Length > 1_048_576
                    && entry.Length / entry.CompressedLength > MaxCompressionRatio)
                {
                    throw new InvalidDataException($"The file '{path}' has an unsafe compression ratio.");
                }

                extractedBytes = checked(extractedBytes + entry.Length);
                if (extractedBytes > options.MaxExtractedBytes)
                {
                    throw new InvalidDataException(
                        $"The extracted package exceeds {options.MaxExtractedBytes} bytes.");
                }

                using var entryStream = entry.Open();
                var bytes = ReadEntry(entryStream, entry.Length, path);
                candidates.Add(new ArchiveFile(path, bytes));
            }

            return StripSharedRoot(candidates);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException
            or OverflowException)
        {
            throw new InvalidDataException("The uploaded ZIP package is invalid.", exception);
        }
    }

    private byte[] ReadEntry(Stream stream, long declaredLength, string path)
    {
        if (declaredLength > int.MaxValue)
        {
            throw new InvalidDataException($"The file '{path}' is too large.");
        }

        using var output = declaredLength > 0
            ? new MemoryStream((int)declaredLength)
            : new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > declaredLength || output.Length > options.MaxExtractedBytes)
            {
                throw new InvalidDataException($"The file '{path}' exceeds its declared size.");
            }
        }
        if (output.Length != declaredLength)
        {
            throw new InvalidDataException($"The file '{path}' has an invalid declared size.");
        }
        return output.ToArray();
    }

    private static IReadOnlyList<SkillPackageFile> StripSharedRoot(List<ArchiveFile> candidates)
    {
        if (candidates.Count == 0) throw new InvalidDataException("The skill package has no files.");

        var rootManifests = candidates.Where(file =>
            string.Equals(file.Path, ManifestName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rootManifests.Length == 1)
        {
            return candidates.Select(file => new SkillPackageFile(
                string.Equals(file.Path, ManifestName, StringComparison.OrdinalIgnoreCase)
                    ? ManifestName
                    : file.Path,
                file.Bytes)).ToArray();
        }
        if (rootManifests.Length > 1)
        {
            throw new InvalidDataException("The package contains multiple SKILL.md files.");
        }

        var firstSeparator = candidates[0].Path.IndexOf('/');
        if (firstSeparator <= 0)
        {
            throw new InvalidDataException("The package must contain SKILL.md at its root.");
        }
        var sharedRoot = candidates[0].Path[..firstSeparator];
        var prefix = sharedRoot + "/";
        if (candidates.Any(file => !file.Path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "SKILL.md may be nested only when every file shares one top-level directory.");
        }

        var stripped = candidates.Select(file => new SkillPackageFile(
            file.Path[prefix.Length..], file.Bytes)).ToArray();
        if (stripped.Count(file =>
                string.Equals(file.Path, ManifestName, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new InvalidDataException("The package must contain exactly one SKILL.md file.");
        }
        return stripped;
    }

    private static ManifestMetadata ParseManifest(string markdown)
    {
        using var reader = new StringReader(markdown);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SKILL.md must begin with YAML frontmatter.");
        }

        string? name = null;
        string? description = null;
        var frontmatterClosed = false;
        var consumed = 0;
        while (reader.ReadLine() is { } line)
        {
            consumed += Encoding.UTF8.GetByteCount(line) + 1;
            if (consumed > MaxFrontmatterBytes)
            {
                throw new InvalidDataException("SKILL.md frontmatter is too large.");
            }
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                frontmatterClosed = true;
                break;
            }
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = ParseYamlScalar(line[(separator + 1)..].Trim());
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
        }

        if (!frontmatterClosed) throw new InvalidDataException("SKILL.md frontmatter is not closed.");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("SKILL.md frontmatter must define a name.");
        }
        if (name.Length > 120) throw new InvalidDataException("The skill name exceeds 120 characters.");
        if ((description?.Length ?? 0) > 1_000)
        {
            throw new InvalidDataException("The skill description exceeds 1000 characters.");
        }

        return new ManifestMetadata(
            name.Trim(),
            description?.Trim() ?? string.Empty,
            reader.ReadToEnd().Trim());
    }

    private static string ParseYamlScalar(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        return value.Trim();
    }

    private static IReadOnlyList<SkillRiskFinding> ScanRisks(
        IReadOnlyList<SkillPackageFile> files)
    {
        var findings = new List<SkillRiskFinding>();
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Path);
            if (BinaryExtensions.Contains(extension))
            {
                findings.Add(new SkillRiskFinding(
                    "binary_file", SkillRiskLevel.Danger, file.Path, null,
                    "The package contains executable or compiled binary content."));
                continue;
            }
            if (!ScriptExtensions.Contains(extension)) continue;

            findings.Add(new SkillRiskFinding(
                "script_file", SkillRiskLevel.Warning, file.Path, null,
                "The package contains a script that requires approval before execution."));
            if (file.Bytes.Length > MaxRiskScanBytes)
            {
                findings.Add(new SkillRiskFinding(
                    "script_scan_truncated", SkillRiskLevel.Warning, file.Path, null,
                    "The script is too large for static line-by-line risk scanning."));
                continue;
            }

            string script;
            try
            {
                script = StrictUtf8.GetString(file.Bytes);
            }
            catch (DecoderFallbackException)
            {
                findings.Add(new SkillRiskFinding(
                    "non_text_script", SkillRiskLevel.Danger, file.Path, null,
                    "The script is not valid UTF-8 text."));
                continue;
            }

            var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                AddMatch(findings, DestructiveCommand(), "destructive_command", SkillRiskLevel.Danger,
                    file.Path, index + 1, line, "The script contains a destructive system command.");
                AddMatch(findings, NetworkAccess(), "network_access", SkillRiskLevel.Danger,
                    file.Path, index + 1, line, "The script attempts network access or data transfer.");
                AddMatch(findings, CredentialAccess(), "credential_access", SkillRiskLevel.Danger,
                    file.Path, index + 1, line, "The script appears to access credentials or secrets.");
                AddMatch(findings, DynamicExecution(), "dynamic_execution", SkillRiskLevel.Warning,
                    file.Path, index + 1, line, "The script dynamically evaluates or executes content.");
                AddMatch(findings, Persistence(), "persistence", SkillRiskLevel.Danger,
                    file.Path, index + 1, line, "The script appears to configure system persistence.");
            }
        }
        return findings;
    }

    private static void AddMatch(
        List<SkillRiskFinding> findings,
        Regex pattern,
        string code,
        SkillRiskLevel severity,
        string path,
        int lineNumber,
        string line,
        string message)
    {
        if (!pattern.IsMatch(line)) return;
        if (findings.Any(finding => finding.Code == code && finding.Path == path)) return;
        findings.Add(new SkillRiskFinding(code, severity, path, lineNumber, message));
    }

    private static string ValidateArchivePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidDataException("The package contains an empty path.");
        }
        if (rawPath.Contains('\\'))
        {
            throw new InvalidDataException($"Backslashes are not allowed in package paths: '{rawPath}'.");
        }
        if (rawPath.StartsWith('/') || Path.IsPathRooted(rawPath)
            || DrivePrefix().IsMatch(rawPath))
        {
            throw new InvalidDataException($"Absolute package paths are not allowed: '{rawPath}'.");
        }

        var directory = rawPath.EndsWith('/');
        var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." || segment.Length > 255 || segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException($"Unsafe package path: '{rawPath}'.");
        }
        var normalized = string.Join('/', segments).Normalize(NormalizationForm.FormC);
        if (normalized.Length > 512 || Encoding.UTF8.GetByteCount(normalized) > 1_024)
        {
            throw new InvalidDataException("A package path exceeds its length limit.");
        }
        return directory ? normalized + "/" : normalized;
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymlink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymlink
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maxBytes,
        CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException($"The upload exceeds {maxBytes} bytes.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }

    private static string Slugify(string name)
    {
        var result = new StringBuilder(Math.Min(name.Length, 80));
        var pendingDash = false;
        foreach (var rune in name.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingDash && result.Length > 0) result.Append('-');
                result.Append(Rune.ToLowerInvariant(rune).ToString());
                pendingDash = false;
                if (result.Length >= 80) break;
            }
            else
            {
                pendingDash = true;
            }
        }
        if (result.Length == 0) throw new InvalidDataException("The skill name cannot form a slug.");
        return result.ToString().TrimEnd('-');
    }

    [GeneratedRegex(@"^[A-Za-z]:[/\\]")]
    private static partial Regex DrivePrefix();

    [GeneratedRegex(@"(?ix)\b(rm\s+-[^\n]*r[^\n]*f|mkfs(?:\.|\s)|dd\s+if=|shutdown|reboot|halt|poweroff)\b")]
    private static partial Regex DestructiveCommand();

    [GeneratedRegex(@"(?ix)\b(curl|wget|nc|ncat|netcat|socat|ssh|scp|ftp)\b|https?://")]
    private static partial Regex NetworkAccess();

    [GeneratedRegex(@"(?ix)(\.ssh/|\.aws/credentials|\.config/gcloud|/etc/shadow|api[_-]?key|access[_-]?token|client[_-]?secret|password)")]
    private static partial Regex CredentialAccess();

    [GeneratedRegex(@"(?ix)\b(eval|exec)\s*\(|\b(python|node|bash|sh)\s+-c\b")]
    private static partial Regex DynamicExecution();

    [GeneratedRegex(@"(?ix)(crontab|/etc/cron|\.config/autostart|systemctl\s+enable|launchctl|authorized_keys)")]
    private static partial Regex Persistence();

    private sealed record ArchiveFile(string Path, byte[] Bytes);
    private sealed record ManifestMetadata(string Name, string Description, string Instructions);
}
