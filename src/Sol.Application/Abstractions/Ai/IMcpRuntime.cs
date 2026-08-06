using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

public interface IMcpRuntime
{
    Task<IReadOnlyList<McpRuntimeTool>> ListToolsAsync(McpServer server, CancellationToken ct);

    Task<McpRuntimeResult> CallToolAsync(
        McpServer server,
        string toolName,
        string argumentsJson,
        CancellationToken ct);
}

public sealed record McpRuntimeTool(
    string Name,
    string Description,
    string InputSchemaJson);

public sealed record McpRuntimeResult(string Json, bool IsError);
