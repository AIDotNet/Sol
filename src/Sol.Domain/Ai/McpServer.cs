using Sol.Domain.Identity;

namespace Sol.Domain.Ai;

public readonly record struct McpServerId(Guid Value)
{
    public static McpServerId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out McpServerId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new McpServerId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An MCP server registration.
/// </summary>
/// <remarks>
/// The transport splits the meaningful fields in two: <see cref="McpTransport.Stdio"/> uses
/// <see cref="Command"/>/<see cref="Args"/>/<see cref="Env"/>/<see cref="Cwd"/>, while the HTTP
/// transports use <see cref="Url"/>/<see cref="Headers"/>. Validation enforces that split.
/// <para>
/// <see cref="Args"/>, <see cref="Env"/> and <see cref="Headers"/> are stored as JSON strings
/// rather than collections: they cross the Dapper boundary as jsonb, and Dapper.AOT supports
/// only statically-typed scalar parameters.
/// </para>
/// </remarks>
public sealed record McpServer(
    McpServerId Id,
    DeviceId DeviceId,
    string Name,
    string? Description,
    bool Enabled,
    McpTransport Transport,
    string? Command,
    string ArgsJson,
    string EnvJson,
    string? Cwd,
    string? Url,
    string HeadersJson,
    bool AutoFallback,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
