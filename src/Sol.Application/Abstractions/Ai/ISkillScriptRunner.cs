namespace Sol.Application.Abstractions.Ai;

/// <summary>Executes an installed Skill script beyond the Sol.Api trust boundary.</summary>
public interface ISkillScriptRunner
{
    int MaximumTimeoutSeconds { get; }
    int MaximumPackageBytes { get; }
    int MaximumFileCount { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct);
}

public sealed record SkillScriptRequest(
    IReadOnlyList<SkillPackageFile> Files,
    string ScriptPath,
    IReadOnlyList<string> Arguments,
    string? Stdin,
    TimeSpan Timeout);

public sealed record SkillScriptResult(
    string Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated);
