using Sol.Domain.Ai;

namespace Sol.UnitTests.Ai;

/// <summary>
/// Base URL normalization.
/// </summary>
/// <remarks>
/// This shipped broken: normalization existed only in the browser, so a provider created
/// through the API directly — or imported from a share link — produced <c>…/v1/v1/messages</c>
/// and every Anthropic call 404'd. The invariant belongs with the code that appends the path,
/// which is what these tests pin down.
/// </remarks>
public class NormalizeBaseUrlTests
{
    [Theory]
    // The client appends /v1/messages, so a pasted /v1 has to come off.
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1/messages", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com")]
    // Case-insensitive, because a hand-typed URL may not match.
    [InlineData("https://api.anthropic.com/V1", "https://api.anthropic.com")]
    public void StripsAnthropicSuffixes(string input, string expected)
    {
        Assert.Equal(expected, AiProvider.NormalizeBaseUrl(input, ProviderType.Anthropic));
    }

    [Theory]
    // Gemini targets its native surface, so the OpenAI-compat suffix has to come off.
    [InlineData("https://generativelanguage.googleapis.com/openai", "https://generativelanguage.googleapis.com")]
    [InlineData("https://generativelanguage.googleapis.com", "https://generativelanguage.googleapis.com")]
    public void StripsGeminiCompatSuffix(string input, string expected)
    {
        Assert.Equal(expected, AiProvider.NormalizeBaseUrl(input, ProviderType.Gemini));
    }

    [Theory]
    // Everything else keeps its /v1 — the OpenAI-compatible clients append only the route.
    [InlineData(ProviderType.OpenAiChat)]
    [InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.OpenAiImages)]
    public void KeepsVersionSegmentForOpenAiShapes(ProviderType protocol)
    {
        Assert.Equal(
            "https://api.openai.com/v1",
            AiProvider.NormalizeBaseUrl("https://api.openai.com/v1", protocol));
    }

    [Fact]
    public void TrimsWhitespaceAndTrailingSlashes()
    {
        Assert.Equal(
            "https://api.example.com/v1",
            AiProvider.NormalizeBaseUrl("  https://api.example.com/v1///  ", ProviderType.OpenAiChat));
    }

    [Fact]
    public void IsIdempotent()
    {
        // Update re-normalizes on every write, so applying it twice must not keep eating path.
        var once = AiProvider.NormalizeBaseUrl("https://api.anthropic.com/v1", ProviderType.Anthropic);
        var twice = AiProvider.NormalizeBaseUrl(once, ProviderType.Anthropic);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void DoesNotStripAVersionSegmentThatIsPartOfTheHost()
    {
        // A relay may legitimately live under a path; only the trailing segment is a suffix.
        Assert.Equal(
            "https://relay.example.com/v1/anthropic",
            AiProvider.NormalizeBaseUrl("https://relay.example.com/v1/anthropic", ProviderType.Anthropic));
    }
}
