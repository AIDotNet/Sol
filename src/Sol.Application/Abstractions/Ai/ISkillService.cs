using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Ai;

public interface ISkillPackageScanner
{
    Task<ScannedSkillPackage> ScanAsync(
        Stream stream,
        string fileName,
        CancellationToken ct);
}

public interface ISkillStore
{
    Task<string> InstallAsync(
        DeviceId deviceId,
        SkillId skillId,
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken ct);

    Task<string> ReadTextAsync(
        string storagePath,
        string relativePath,
        int maxBytes,
        CancellationToken ct);

    Task<IReadOnlyList<string>> ListFilesAsync(
        string storagePath,
        int maxEntries,
        CancellationToken ct);

    Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(
        string storagePath,
        int maxEntries,
        int maxBytes,
        CancellationToken ct);

    Task DeleteAsync(string storagePath, CancellationToken ct);
}

public sealed record SkillRiskFinding(
    string Code,
    SkillRiskLevel Severity,
    string Path,
    int? Line,
    string Message);

public sealed record SkillPackageFile(string Path, byte[] Bytes);

public sealed record ScannedSkillPackage(
    string Name,
    string Slug,
    string Description,
    string Instructions,
    IReadOnlyList<SkillPackageFile> Files,
    IReadOnlyList<SkillRiskFinding> Risks,
    bool HasScripts,
    SkillRiskLevel MaxRisk,
    long ByteSize);
