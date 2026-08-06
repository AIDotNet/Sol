using Sol.Domain.Identity;

namespace Sol.Domain.Ai;

public readonly record struct SkillId(Guid Value)
{
    public static SkillId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out SkillId id)
    {
        if (Guid.TryParse(text, out var value) && value != Guid.Empty)
        {
            id = new SkillId(value);
            return true;
        }
        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public enum SkillRiskLevel
{
    Safe,
    Warning,
    Danger,
}

public sealed record Skill(
    SkillId Id,
    DeviceId DeviceId,
    string Slug,
    string Name,
    string Description,
    string StoragePath,
    bool Enabled,
    bool HasScripts,
    string RiskJson,
    SkillRiskLevel MaxRisk,
    long ByteSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
