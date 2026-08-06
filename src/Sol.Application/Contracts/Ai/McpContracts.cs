namespace Sol.Application.Contracts.Ai;

public sealed record McpServerResponse(
    string Id,
    string Name,
    string? Description,
    bool Enabled,
    string Transport,
    string? Command,
    string[] Args,
    McpSecretEntry[] Env,
    string? Cwd,
    string? Url,
    McpSecretEntry[] Headers,
    bool AutoFallback,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// One key/value pair of environment or header configuration.
/// </summary>
/// <remarks>
/// A pair array rather than a dictionary: <c>Dictionary&lt;string, string&gt;</c> serializes fine
/// under source generation, but an array keeps ordering stable for the settings UI, which
/// renders these as an editable list where rows jumping around on every save is disorienting.
/// </remarks>
public sealed record McpEnvEntry(string Key, string Value);

/// <summary>Secret metadata returned to the browser; the value itself never leaves the server.</summary>
public sealed record McpSecretEntry(string Key, bool IsConfigured, string Hint);

public sealed record McpServerListResponse(McpServerResponse[] Servers);

public sealed record CreateMcpServerRequest(
    string Name,
    string? Description,
    string Transport,
    string? Command,
    string[]? Args,
    McpEnvEntry[]? Env,
    string? Cwd,
    string? Url,
    McpEnvEntry[]? Headers,
    bool? AutoFallback,
    bool? Enabled);

public sealed record UpdateMcpServerRequest(
    string? Name,
    string? Description,
    string? Transport,
    string? Command,
    string[]? Args,
    McpEnvEntry[]? Env,
    string? Cwd,
    string? Url,
    McpEnvEntry[]? Headers,
    bool? AutoFallback,
    bool? Enabled);

/// <summary>Bulk import; servers whose names already exist are skipped.</summary>
public sealed record ImportMcpServersRequest(CreateMcpServerRequest[] Servers);

public sealed record ImportMcpServersResponse(int Added);

public sealed record McpServerCheckResponse(bool Ok, string? Error, int ToolCount);

public sealed record McpToolResponse(string Name, string Description, string InputSchemaJson);

public sealed record McpToolListResponse(McpToolResponse[] Tools);
