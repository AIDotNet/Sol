using Sol.Domain.Identity;

namespace Sol.Domain.Ai;

public readonly record struct ProviderId(Guid Value)
{
    public static ProviderId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out ProviderId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new ProviderId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public readonly record struct ModelId(Guid Value)
{
    public static ModelId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out ModelId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new ModelId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An encrypted API key. AES-GCM, so the ciphertext carries its own integrity tag.
/// </summary>
/// <remarks>
/// The three parts travel together because they are useless apart: decryption needs the nonce
/// to derive the keystream and the tag to prove the ciphertext was not altered. Keeping them in
/// one type makes it impossible to persist a ciphertext and forget its nonce.
/// <para>
/// <see cref="Hint"/> is the only part safe to show a user, and is the only part that ever
/// leaves the server.
/// </para>
/// </remarks>
public sealed record ApiKeySecret(byte[] Cipher, byte[] Nonce, byte[] Tag, string Hint)
{
    /// <summary>
    /// Builds the display hint for a key. Shows at most the last four characters; a key short
    /// enough that four characters would be a meaningful fraction of it is fully masked.
    /// </summary>
    public static string BuildHint(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Length <= 8 ? "••••" : $"••••{trimmed[^4..]}";
    }
}

public sealed record AiModel(
    ModelId Id,
    ProviderId ProviderId,
    string ModelKey,
    string Name,
    bool Enabled,
    ProviderType? Type,
    ModelCategory Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool SupportsVision,
    bool SupportsFunctionCall,
    bool SupportsThinking,
    int SortOrder,
    DateTimeOffset CreatedAt);

public sealed record AiProvider(
    ProviderId Id,
    DeviceId DeviceId,
    string? BuiltinId,
    string Name,
    string? Description,
    string? Icon,
    ProviderType Type,
    ApiKeySecret? ApiKey,
    string BaseUrl,
    bool Enabled,
    int? PresetVersion,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<AiModel> Models { get; init; } = [];

    public bool HasApiKey => ApiKey is not null;

    /// <summary>
    /// Resolves the protocol to use for one of this provider's models.
    /// </summary>
    /// <remarks>
    /// An explicit override on the model always wins. Otherwise an image or video model falls
    /// back to a media protocol rather than the provider's, because a provider is declared by
    /// its chat protocol and its image endpoint speaks something else — an OpenAI-compatible
    /// provider is <c>openai-chat</c>, but its image route is <c>openai-images</c>.
    /// <para>
    /// The frontend implements the identical rule in <c>web/features/ai/types.ts</c>. The two
    /// must agree: the client uses it to decide which parameter controls to render, the server
    /// to decide which client to dispatch to.
    /// </para>
    /// </remarks>
    public ProviderType ResolveProtocol(AiModel? model)
    {
        if (model is null)
        {
            return Type;
        }

        if (model.Type is { } explicitType)
        {
            return explicitType;
        }

        return model.Category switch
        {
            ModelCategory.Image => ProviderType.OpenAiImages,
            ModelCategory.Video => ProviderType.SeedanceVideo,
            _ => Type,
        };
    }

    /// <summary>
    /// Trims the suffixes a protocol client re-appends itself.
    /// </summary>
    /// <remarks>
    /// Users paste whatever the vendor's docs show, which is usually the OpenAI-compatible URL.
    /// Without this an Anthropic base URL of <c>https://api.anthropic.com/v1</c> becomes
    /// <c>…/v1/v1/messages</c>.
    /// <para>
    /// Enforced here rather than only in the browser because the invariant belongs with the
    /// code that appends the path: the API is reachable directly, and a config imported from a
    /// share link should not depend on the sender having normalized it.
    /// </para>
    /// </remarks>
    public static string NormalizeBaseUrl(string baseUrl, ProviderType protocol)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        return protocol switch
        {
            // The client appends /v1/messages.
            ProviderType.Anthropic => StripSuffix(StripSuffix(trimmed, "/messages"), "/v1"),

            // The client appends /v1beta/models/... — an OpenAI-compat suffix would break it.
            ProviderType.Gemini => StripSuffix(trimmed, "/openai"),

            _ => trimmed,
        };
    }

    private static string StripSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length].TrimEnd('/')
            : value;
}
