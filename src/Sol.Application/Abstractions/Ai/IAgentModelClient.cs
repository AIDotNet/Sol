using System.Runtime.CompilerServices;
using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Streams one model turn that may contain text, thinking, and tool-use content blocks.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ITextGenerationClient"/>. Prompt rewriting intentionally has a
/// short, non-streaming contract; an agent turn is multi-message and preserves structured blocks
/// for the next iteration, so widening that interface would make every protocol implement features
/// its caller cannot use.
/// </remarks>
public interface IAgentModelClient
{
    ProviderType Protocol { get; }

    IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        CancellationToken ct);
}

public interface IAgentModelDispatcher
{
    IAsyncEnumerable<AgentModelEvent> StreamAsync(
        ProviderType protocol,
        AgentModelRequest request,
        CancellationToken ct);
}

public enum AgentMessageRole
{
    User,
    Assistant,
}

public enum AgentContentKind
{
    Text,
    Image,
    Thinking,
    ToolUse,
    ToolResult,
}

public sealed record AgentContentBlock(
    AgentContentKind Kind,
    string? Text = null,
    string? ToolUseId = null,
    string? ToolName = null,
    string? Json = null,
    bool IsError = false,
    string? ProviderItemId = null,
    string? ImageUrl = null,
    string? MediaType = null,
    byte[]? ImageBytes = null);

public sealed record AgentModelMessage(
    AgentMessageRole Role,
    IReadOnlyList<AgentContentBlock> Content);

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson);

public sealed record AgentModelRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string SystemPrompt,
    IReadOnlyList<AgentModelMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    int MaxOutputTokens,
    bool SupportsThinking = false);

public enum AgentModelEventKind
{
    TextDelta,
    ThinkingDelta,
    ToolUse,
    Completed,
    Failed,
}

/// <param name="Json">
/// Complete tool input JSON for <see cref="AgentModelEventKind.ToolUse"/>. Protocol clients
/// accumulate partial argument fragments and only emit after the complete JSON object is available.
/// </param>
public sealed record AgentModelEvent(
    AgentModelEventKind Kind,
    string? Text = null,
    string? ToolUseId = null,
    string? ToolName = null,
    string? Json = null,
    string? StopReason = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? Error = null,
    string? ProviderItemId = null);
