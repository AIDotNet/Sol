using Npgsql;
using Sol.Application.Abstractions.Identity;

namespace Sol.Infrastructure.Persistence;

/// <summary>
/// Opens a pooled connection and applies the validated account scope for this request.
/// </summary>
/// <remarks>
/// PostgreSQL connections are pooled, so the setting is overwritten on every checkout. An empty
/// value means guest/device-only scope; it is never left over from the previous request that used
/// the same physical connection.
/// </remarks>
public sealed class SolConnectionFactory(
    NpgsqlDataSource dataSource,
    IAccountContext accountContext)
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT set_config('sol.account_id', $1, false)";
            command.Parameters.AddWithValue(
                accountContext.AccountId?.Value.ToString() ?? string.Empty);
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class AccountContext : IAccountContext
{
    public Sol.Domain.Identity.AccountId? AccountId { get; set; }
}
