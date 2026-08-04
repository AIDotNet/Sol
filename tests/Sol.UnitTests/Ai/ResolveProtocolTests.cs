using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.UnitTests.Ai;

/// <summary>
/// Protocol resolution: <c>model.Type ?? category default ?? provider.Type</c>.
/// </summary>
/// <remarks>
/// This rule is implemented twice — here and in <c>web/features/ai/types.ts</c> — because the
/// client uses it to decide which parameter controls to render and the server uses it to pick a
/// dispatch target. If they disagree, a user configures one thing and a different one runs.
/// </remarks>
public class ResolveProtocolTests
{
    private static AiProvider Provider(ProviderType type) => new(
        ProviderId.New(),
        DeviceId.New(),
        BuiltinId: null,
        Name: "p",
        Description: null,
        Icon: null,
        type,
        ApiKey: null,
        BaseUrl: "https://example.com",
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    private static AiModel Model(ProviderType? type, ModelCategory category) => new(
        ModelId.New(),
        ProviderId.New(),
        ModelKey: "m",
        Name: "M",
        Enabled: true,
        type,
        category,
        Icon: null,
        ContextLength: null,
        MaxOutputTokens: null,
        SupportsVision: false,
        SupportsFunctionCall: false,
        SupportsThinking: false,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void AnExplicitOverrideAlwaysWins()
    {
        // This is what lets one aggregator provider serve models across several protocols.
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.Anthropic,
            provider.ResolveProtocol(Model(ProviderType.Anthropic, ModelCategory.Chat)));

        Assert.Equal(
            ProviderType.OpenAiResponses,
            provider.ResolveProtocol(Model(ProviderType.OpenAiResponses, ModelCategory.Chat)));
    }

    [Fact]
    public void AnOverrideWinsEvenAgainstTheCategoryDefault()
    {
        // Gemini serves images from its own endpoint, not an OpenAI-images one.
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.Gemini,
            provider.ResolveProtocol(Model(ProviderType.Gemini, ModelCategory.Image)));
    }

    [Fact]
    public void AnImageModelDefaultsToTheImageProtocolRatherThanTheProviders()
    {
        // A provider is declared by its chat protocol; its image route speaks something else.
        // Falling back to provider.Type here would send an images request to /chat/completions.
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.OpenAiImages,
            provider.ResolveProtocol(Model(null, ModelCategory.Image)));
    }

    [Fact]
    public void AVideoModelDefaultsToTheVideoProtocol()
    {
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.SeedanceVideo,
            provider.ResolveProtocol(Model(null, ModelCategory.Video)));
    }

    [Fact]
    public void AChatModelInheritsTheProvider()
    {
        Assert.Equal(
            ProviderType.Anthropic,
            Provider(ProviderType.Anthropic).ResolveProtocol(Model(null, ModelCategory.Chat)));
    }

    [Fact]
    public void NoModelMeansTheProvidersOwnProtocol()
    {
        // Used by the connectivity check, which has no particular model in hand.
        Assert.Equal(
            ProviderType.Gemini,
            Provider(ProviderType.Gemini).ResolveProtocol(null));
    }
}
