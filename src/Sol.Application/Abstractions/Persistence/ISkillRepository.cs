using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

public interface ISkillRepository
{
    Task<IReadOnlyList<Skill>> ListAsync(DeviceId deviceId, CancellationToken ct);
    Task<Skill?> FindAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct);
    Task<Skill?> FindBySlugAsync(DeviceId deviceId, string slug, CancellationToken ct);
    Task<bool> InsertAsync(Skill skill, CancellationToken ct);
    Task<bool> DeleteAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct);
}
