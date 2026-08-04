namespace Sol.Domain.Ai;

/// <summary>
/// The wire protocol used to talk to a model. Describes how a request is shaped, not who serves
/// it — several vendors speak <c>OpenAiChat</c>, and one aggregator can serve models across
/// three protocols at once.
/// </summary>
/// <remarks>
/// Persisted as text (see <c>db/migrations/0003__ai_config.sql</c>) rather than by ordinal, so
/// reordering this enum cannot silently reinterpret existing rows.
/// </remarks>
public enum ProviderType
{
    OpenAiChat,
    OpenAiResponses,
    Anthropic,
    Gemini,
    OpenAiImages,
    OpenAiVideo,
    SeedanceVideo,
    XaiVideo,
}

/// <summary>What a model is used for. Determines which node types can select it.</summary>
public enum ModelCategory
{
    Chat,
    Image,
    Video,
    Embedding,
    Speech,
}

public enum McpTransport
{
    Stdio,
    Sse,
    StreamableHttp,
}

/// <summary>
/// Converts between the enums above and the exact strings stored in Postgres.
/// </summary>
/// <remarks>
/// Hand-written rather than <see cref="System.Enum.Parse(System.Type, string)"/>: reflection-based
/// enum parsing is a trimming hazard under Native AOT, and the wire strings are kebab-case, which
/// no built-in naming policy produces. Both directions live together so a new protocol cannot be
/// added to one and forgotten in the other.
/// </remarks>
public static class AiEnumNames
{
    public static string ToWire(this ProviderType value) => value switch
    {
        ProviderType.OpenAiChat => "openai-chat",
        ProviderType.OpenAiResponses => "openai-responses",
        ProviderType.Anthropic => "anthropic",
        ProviderType.Gemini => "gemini",
        ProviderType.OpenAiImages => "openai-images",
        ProviderType.OpenAiVideo => "openai-video",
        ProviderType.SeedanceVideo => "seedance-video",
        ProviderType.XaiVideo => "xai-video",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown provider type."),
    };

    public static bool TryParseProviderType(string? text, out ProviderType value)
    {
        switch (text)
        {
            case "openai-chat": value = ProviderType.OpenAiChat; return true;
            case "openai-responses": value = ProviderType.OpenAiResponses; return true;
            case "anthropic": value = ProviderType.Anthropic; return true;
            case "gemini": value = ProviderType.Gemini; return true;
            case "openai-images": value = ProviderType.OpenAiImages; return true;
            case "openai-video": value = ProviderType.OpenAiVideo; return true;
            case "seedance-video": value = ProviderType.SeedanceVideo; return true;
            case "xai-video": value = ProviderType.XaiVideo; return true;
            default: value = default; return false;
        }
    }

    public static string ToWire(this ModelCategory value) => value switch
    {
        ModelCategory.Chat => "chat",
        ModelCategory.Image => "image",
        ModelCategory.Video => "video",
        ModelCategory.Embedding => "embedding",
        ModelCategory.Speech => "speech",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown model category."),
    };

    public static bool TryParseModelCategory(string? text, out ModelCategory value)
    {
        switch (text)
        {
            case "chat": value = ModelCategory.Chat; return true;
            case "image": value = ModelCategory.Image; return true;
            case "video": value = ModelCategory.Video; return true;
            case "embedding": value = ModelCategory.Embedding; return true;
            case "speech": value = ModelCategory.Speech; return true;
            default: value = default; return false;
        }
    }

    public static string ToWire(this McpTransport value) => value switch
    {
        McpTransport.Stdio => "stdio",
        McpTransport.Sse => "sse",
        McpTransport.StreamableHttp => "streamable-http",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown MCP transport."),
    };

    public static bool TryParseMcpTransport(string? text, out McpTransport value)
    {
        switch (text)
        {
            case "stdio": value = McpTransport.Stdio; return true;
            case "sse": value = McpTransport.Sse; return true;
            case "streamable-http": value = McpTransport.StreamableHttp; return true;
            default: value = default; return false;
        }
    }
}
