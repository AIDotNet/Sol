using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

public interface IMcpServerRepository
{
    Task<IReadOnlyList<McpServer>> ListAsync(DeviceId deviceId, CancellationToken ct);

    Task<McpServer?> FindAsync(DeviceId deviceId, McpServerId serverId, CancellationToken ct);

    Task InsertAsync(McpServer server, CancellationToken ct);

    Task UpdateAsync(McpServer server, CancellationToken ct);

    Task<bool> DeleteAsync(DeviceId deviceId, McpServerId serverId, CancellationToken ct);

    /// <summary>
    /// Inserts servers whose names are not already registered for the device, returning how many
    /// were added. Backs JSON bulk import, which users re-run against an edited file.
    /// </summary>
    Task<int> InsertIfAbsentAsync(IReadOnlyList<McpServer> servers, CancellationToken ct);
}
