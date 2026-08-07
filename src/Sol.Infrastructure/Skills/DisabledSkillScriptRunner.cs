using Sol.Application.Abstractions.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Skills;

/// <summary>Refuses Skill script execution when no isolated sandbox is configured.</summary>
internal sealed class DisabledSkillScriptRunner(SkillsOptions options) : ISkillScriptRunner
{
    public int MaximumTimeoutSeconds => options.RunnerTimeoutSeconds;
    public int MaximumPackageBytes => Math.Min(
        options.RunnerMaxPackageBytes,
        Math.Min(options.MaxUploadBytes, options.MaxExtractedBytes));
    public int MaximumFileCount => options.MaxEntries;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);

    public Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct) =>
        Task.FromException<SkillScriptResult>(new InvalidOperationException(
            "Skill script execution requires an enabled sandbox."));
}