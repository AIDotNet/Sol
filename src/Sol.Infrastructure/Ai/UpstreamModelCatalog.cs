using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

/// <summary>
/// Fetches a provider's model catalog over HTTP.
/// </summary>
/// <remarks>
/// Responses are read with <see cref="JsonDocument"/> rather than deserialized into DTOs. Every
/// vendor returns a slightly different envelope and adds fields freely; a DTO per protocol would
/// have to be registered in a <c>JsonSerializerContext</c> and would break whenever a vendor
/// changed shape. Reading the two fields we actually need is both AOT-safe and resilient.
/// </remarks>
internal sealed partial class UpstreamModelCatalog(
    IHttpClientFactory httpClientFactory,
    ILogger<UpstreamModelCatalog> logger) : IUpstreamModelCatalog
{
    public async Task<UpstreamCatalogResult> ListModelsAsync(
        AiProvider provider,
        string apiKey,
        CancellationToken ct)
    {
        var protocol = provider.Type;
        var baseUrl = provider.BaseUrl.TrimEnd('/');

        var (url, request) = protocol switch
        {
            ProviderType.Anthropic => BuildAnthropic(baseUrl, apiKey),
            ProviderType.Gemini => BuildGemini(baseUrl, apiKey),
            _ => BuildOpenAiCompatible(baseUrl, apiKey),
        };

        var client = httpClientFactory.CreateClient("upstream");

        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Model catalog request to {Url} failed with {StatusCode}", url, (int)response.StatusCode);

                return UpstreamCatalogResult.Failure((int)response.StatusCode, Summarize(body));
            }

            return UpstreamCatalogResult.Success(Parse(body, protocol));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpstreamCatalogResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            // The base URL is user-supplied, so an unreachable host is an expected outcome
            // rather than a fault. Report it back instead of surfacing a 500.
            return UpstreamCatalogResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return UpstreamCatalogResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }

    private static (string Url, HttpRequestMessage Request) BuildOpenAiCompatible(
        string baseUrl,
        string apiKey)
    {
        var url = $"{baseUrl}/models";
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return (url, request);
    }

    private static (string Url, HttpRequestMessage Request) BuildAnthropic(
        string baseUrl,
        string apiKey)
    {
        // BaseUrl is normalized to drop a trailing /v1, so it is re-added here.
        var url = $"{baseUrl}/v1/models";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        return (url, request);
    }

    private static (string Url, HttpRequestMessage Request) BuildGemini(string baseUrl, string apiKey)
    {
        var url = $"{baseUrl}/v1beta/models";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

        return (url, request);
    }

    private static IReadOnlyList<UpstreamModel> Parse(string body, ProviderType protocol)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        // OpenAI-compatible and Anthropic both nest under "data"; Gemini uses "models".
        var arrayProperty = protocol == ProviderType.Gemini ? "models" : "data";

        if (!root.TryGetProperty(arrayProperty, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<UpstreamModel>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(element, "id") ?? ReadString(element, "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // Gemini returns fully-qualified resource names ("models/gemini-2.5-flash").
            var modelKey = id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id;

            var displayName = ReadString(element, "display_name")
                ?? ReadString(element, "displayName")
                ?? modelKey;

            models.Add(new UpstreamModel(modelKey, displayName, InferCategory(modelKey)));
        }

        return models;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Guesses what a model is for from its identifier.
    /// </summary>
    /// <remarks>
    /// Most catalog endpoints report no category at all, and the canvas needs one to decide
    /// which nodes may select the model. A wrong guess is correctable in settings; refusing to
    /// import anything unrecognised would be worse, so the fallback is <c>chat</c>.
    /// </remarks>
    private static ModelCategory InferCategory(string modelKey)
    {
        if (VideoPattern().IsMatch(modelKey)) return ModelCategory.Video;
        if (ImagePattern().IsMatch(modelKey)) return ModelCategory.Image;
        if (EmbeddingPattern().IsMatch(modelKey)) return ModelCategory.Embedding;
        if (SpeechPattern().IsMatch(modelKey)) return ModelCategory.Speech;

        return ModelCategory.Chat;
    }

    /// <summary>Truncates an upstream error body so a verbose HTML page cannot flood the UI or logs.</summary>
    private static string Summarize(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0) return "Upstream returned an empty error body.";

        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }

    // Source-generated: a compiled regex avoids the interpreter, which is a trimming hazard.
    [GeneratedRegex(@"(sora|veo|seedance|kling|runway|video|wan2)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPattern();

    [GeneratedRegex(@"(dall-e|gpt-image|imagen|seedream|flux|stable-diffusion|sdxl|kolors|image)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"(embedding|embed|bge-|gte-|text-embedding)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddingPattern();

    [GeneratedRegex(@"(whisper|tts|speech|audio|voice)", RegexOptions.IgnoreCase)]
    private static partial Regex SpeechPattern();
}
