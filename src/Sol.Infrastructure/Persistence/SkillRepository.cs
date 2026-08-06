using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

public sealed class SkillRepository(NpgsqlDataSource dataSource) : ISkillRepository
{
    private const string Columns = """
        skill_id, device_id, slug, name, description, storage_path, enabled, has_scripts,
        risk::text AS risk_json, max_risk, byte_size, created_at, updated_at
        """;

    public async Task<IReadOnlyList<Skill>> ListAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SkillRow>(
            $"SELECT {Columns} FROM skill WHERE device_id = @DeviceId ORDER BY name",
            new DeviceIdParam { DeviceId = deviceId.Value });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<Skill?> FindAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<SkillRow>(
            $"SELECT {Columns} FROM skill WHERE skill_id = @SkillId AND device_id = @DeviceId",
            new SkillScopeParams { SkillId = skillId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    public async Task<Skill?> FindBySlugAsync(DeviceId deviceId, string slug, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<SkillRow>(
            $"SELECT {Columns} FROM skill WHERE slug = @Slug AND device_id = @DeviceId",
            new SkillSlugParams { Slug = slug, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    public async Task<bool> InsertAsync(Skill skill, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            INSERT INTO skill
                (skill_id, device_id, slug, name, description, storage_path, enabled,
                 has_scripts, risk, max_risk, byte_size, created_at, updated_at)
            VALUES
                (@SkillId, @DeviceId, @Slug, @Name, @Description, @StoragePath, @Enabled,
                 @HasScripts, @RiskJson::jsonb, @MaxRisk, @ByteSize, @CreatedAt, @UpdatedAt)
            ON CONFLICT (device_id, slug) DO NOTHING
            """,
            SkillParams.From(skill));
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            "DELETE FROM skill WHERE skill_id = @SkillId AND device_id = @DeviceId",
            new SkillScopeParams { SkillId = skillId.Value, DeviceId = deviceId.Value });
        return affected > 0;
    }

    internal static string ToWire(SkillRiskLevel level) => level switch
    {
        SkillRiskLevel.Safe => "safe",
        SkillRiskLevel.Warning => "warning",
        SkillRiskLevel.Danger => "danger",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    internal static SkillRiskLevel ParseRisk(string value) => value switch
    {
        "safe" => SkillRiskLevel.Safe,
        "warning" => SkillRiskLevel.Warning,
        "danger" => SkillRiskLevel.Danger,
        _ => throw new InvalidOperationException($"Unknown skill risk '{value}'."),
    };
}

internal sealed class SkillScopeParams { public Guid SkillId { get; init; } public Guid DeviceId { get; init; } }
internal sealed class SkillSlugParams { public string Slug { get; init; } = string.Empty; public Guid DeviceId { get; init; } }
internal sealed class SkillParams
{
    public Guid SkillId { get; init; }
    public Guid DeviceId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool HasScripts { get; init; }
    public string RiskJson { get; init; } = "[]";
    public string MaxRisk { get; init; } = "safe";
    public long ByteSize { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public static SkillParams From(Skill skill) => new()
    {
        SkillId = skill.Id.Value,
        DeviceId = skill.DeviceId.Value,
        Slug = skill.Slug,
        Name = skill.Name,
        Description = skill.Description,
        StoragePath = skill.StoragePath,
        Enabled = skill.Enabled,
        HasScripts = skill.HasScripts,
        RiskJson = skill.RiskJson,
        MaxRisk = SkillRepository.ToWire(skill.MaxRisk),
        ByteSize = skill.ByteSize,
        CreatedAt = skill.CreatedAt,
        UpdatedAt = skill.UpdatedAt,
    };
}

internal sealed class SkillRow
{
    public Guid SkillId { get; init; }
    public Guid DeviceId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool HasScripts { get; init; }
    public string RiskJson { get; init; } = "[]";
    public string MaxRisk { get; init; } = "safe";
    public long ByteSize { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public Skill ToDomain() => new(
        new SkillId(SkillId), new DeviceId(DeviceId), Slug, Name, Description, StoragePath,
        Enabled, HasScripts, RiskJson, SkillRepository.ParseRisk(MaxRisk), ByteSize,
        Utc(CreatedAt), Utc(UpdatedAt));

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
