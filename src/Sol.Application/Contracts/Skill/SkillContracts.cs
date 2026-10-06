namespace Sol.Application.Contracts.Skill;

public sealed record SkillRiskFindingResponse(
    string Code,
    string Severity,
    string Path,
    int? Line,
    string Message);

public sealed record SkillScanResponse(
    string Name,
    string Slug,
    string Description,
    bool HasScripts,
    string MaxRisk,
    SkillRiskFindingResponse[] Risks,
    string[] Files,
    long ByteSize);

public sealed record SkillResponse(
    string Id,
    string Slug,
    string Name,
    string Description,
    bool Enabled,
    bool HasScripts,
    string MaxRisk,
    SkillRiskFindingResponse[] Risks,
    long ByteSize,
    string CreatedAt,
    string UpdatedAt);

public sealed record SkillListResponse(bool SandboxEnabled, SkillResponse[] Skills);

/// <summary>A read-only Skill shipped with Sol. No id, no risk metadata — it cannot be uninstalled.</summary>
public sealed record BuiltInSkillResponse(
    string Slug,
    string Name,
    string Description,
    string[] Files);

public sealed record BuiltInSkillListResponse(BuiltInSkillResponse[] Skills);
