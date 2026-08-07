using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

/// <remarks>
/// <c>args</c>, <c>env</c> and <c>headers</c> cross the boundary as JSON text and are cast to
/// jsonb in SQL. Npgsql's <c>EnableDynamicJson</c> is unavailable under AOT, so the conversion
/// stays explicit rather than relying on a runtime type mapper.
/// </remarks>
public sealed class McpServerRepository(SolConnectionFactory connections) : IMcpServerRepository
{
    private const string Columns = """
        server_id, device_id, name, description, enabled, transport, command,
        args::text AS args_json, env::text AS env_json, cwd, url,
        headers::text AS headers_json, auto_fallback, created_at, updated_at
        """;

    public async Task<IReadOnlyList<McpServer>> ListAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<McpServerRow>(
            $"""
            SELECT {Columns}
            FROM mcp_server
            WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            ORDER BY created_at
            """,
            new DeviceIdParam { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<McpServer?> FindAsync(
        DeviceId deviceId,
        McpServerId serverId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<McpServerRow>(
            $"""
            SELECT {Columns}
            FROM mcp_server
            WHERE server_id = @ServerId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new McpScopeParam { ServerId = serverId.Value, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    public async Task InsertAsync(McpServer server, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await InsertCoreAsync(connection, server, onConflictDoNothing: false);
    }

    public async Task<int> InsertIfAbsentAsync(
        IReadOnlyList<McpServer> servers,
        CancellationToken ct)
    {
        if (servers.Count == 0)
        {
            return 0;
        }

        await using var connection = await connections.OpenAsync(ct);

        var added = 0;
        foreach (var server in servers)
        {
            added += await InsertCoreAsync(connection, server, onConflictDoNothing: true);
        }

        return added;
    }

    private static async Task<int> InsertCoreAsync(
        NpgsqlConnection connection,
        McpServer server,
        bool onConflictDoNothing)
    {
        var conflict = onConflictDoNothing ? "ON CONFLICT (device_id, name) DO NOTHING" : "";

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO mcp_server (server_id, device_id, name, description, enabled, transport,
                                    command, args, env, cwd, url, headers, auto_fallback,
                                    created_at, updated_at)
            VALUES (@ServerId, @DeviceId, @Name, @Description, @Enabled, @Transport,
                    @Command, @ArgsJson::jsonb, @EnvJson::jsonb, @Cwd, @Url,
                    @HeadersJson::jsonb, @AutoFallback, @CreatedAt, @UpdatedAt)
            {conflict}
            """,
            new InsertMcpParams
            {
                ServerId = server.Id.Value,
                DeviceId = server.DeviceId.Value,
                Name = server.Name,
                Description = server.Description,
                Enabled = server.Enabled,
                Transport = server.Transport.ToWire(),
                Command = server.Command,
                ArgsJson = server.ArgsJson,
                EnvJson = server.EnvJson,
                Cwd = server.Cwd,
                Url = server.Url,
                HeadersJson = server.HeadersJson,
                AutoFallback = server.AutoFallback,
                CreatedAt = server.CreatedAt,
                UpdatedAt = server.UpdatedAt,
            });
    }

    public async Task UpdateAsync(McpServer server, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            UPDATE mcp_server
            SET name = @Name, description = @Description, enabled = @Enabled,
                transport = @Transport, command = @Command, args = @ArgsJson::jsonb,
                env = @EnvJson::jsonb, cwd = @Cwd, url = @Url,
                headers = @HeadersJson::jsonb, auto_fallback = @AutoFallback,
                updated_at = @UpdatedAt
            WHERE server_id = @ServerId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new UpdateMcpParams
            {
                ServerId = server.Id.Value,
                DeviceId = server.DeviceId.Value,
                Name = server.Name,
                Description = server.Description,
                Enabled = server.Enabled,
                Transport = server.Transport.ToWire(),
                Command = server.Command,
                ArgsJson = server.ArgsJson,
                EnvJson = server.EnvJson,
                Cwd = server.Cwd,
                Url = server.Url,
                HeadersJson = server.HeadersJson,
                AutoFallback = server.AutoFallback,
                UpdatedAt = server.UpdatedAt,
            });
    }

    public async Task<bool> DeleteAsync(
        DeviceId deviceId,
        McpServerId serverId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM mcp_server WHERE server_id = @ServerId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))",
            new McpScopeParam { ServerId = serverId.Value, DeviceId = deviceId.Value });

        return affected > 0;
    }
}

internal sealed class McpScopeParam
{
    public Guid ServerId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class InsertMcpParams
{
    public Guid ServerId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public string Transport { get; init; } = string.Empty;
    public string? Command { get; init; }
    public string ArgsJson { get; init; } = "[]";
    public string EnvJson { get; init; } = "{}";
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public string HeadersJson { get; init; } = "{}";
    public bool AutoFallback { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateMcpParams
{
    public Guid ServerId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public string Transport { get; init; } = string.Empty;
    public string? Command { get; init; }
    public string ArgsJson { get; init; } = "[]";
    public string EnvJson { get; init; } = "{}";
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public string HeadersJson { get; init; } = "{}";
    public bool AutoFallback { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class McpServerRow
{
    public Guid ServerId { get; init; }
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public string Transport { get; init; } = string.Empty;
    public string? Command { get; init; }
    public string ArgsJson { get; init; } = "[]";
    public string EnvJson { get; init; } = "{}";
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public string HeadersJson { get; init; } = "{}";
    public bool AutoFallback { get; init; }

    // timestamptz reads back as a UTC DateTime; a DateTimeOffset here would make Dapper attempt
    // a Convert.ChangeType that does not exist and throw at runtime. See ProviderRow.
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public McpServer ToDomain()
    {
        if (!AiEnumNames.TryParseMcpTransport(Transport, out var transport))
        {
            throw new InvalidOperationException(
                $"MCP server {ServerId} has unrecognised transport '{Transport}'.");
        }

        return new McpServer(
            new McpServerId(ServerId),
            new DeviceId(DeviceId),
            Name,
            Description,
            Enabled,
            transport,
            Command,
            ArgsJson,
            EnvJson,
            Cwd,
            Url,
            HeadersJson,
            AutoFallback,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }
}
