using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// Gemini image generation via <c>generateContent</c>.
/// </summary>
/// <remarks>
/// Gemini has no dedicated images endpoint: an image model is called the same way a chat model
/// is, and the picture comes back as an <c>inlineData</c> part alongside any text. Reference
/// images are extra parts on the same user turn, so editing and generating are one request shape
/// rather than two.
/// <para>
/// It also has no <c>n</c> parameter — the caller loops when it wants several images.
/// </para>
/// </remarks>
internal sealed class GeminiImagesClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GeminiImagesClient> logger) : IImageGenerationClient
{
    public ProviderType Protocol => ProviderType.Gemini;

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1beta/models/{request.ModelKey}:generateContent";

        var images = new List<GeneratedImage>();

        // Sequential rather than parallel: the requests share one API key and quota, and a burst
        // of concurrent calls is the fastest way to trigger rate limiting.
        for (var i = 0; i < request.Count; i++)
        {
            var result = await GenerateOneAsync(client, url, request, ct);

            if (!result.Ok)
            {
                // Anything already produced is worth returning; a partial result beats none.
                return images.Count > 0
                    ? ImageGenerationResult.Success(images)
                    : result;
            }

            images.AddRange(result.Images);
        }

        return images.Count > 0
            ? ImageGenerationResult.Success(images)
            : ImageGenerationResult.Failure(null, "Upstream returned no image data.");
    }

    private async Task<ImageGenerationResult> GenerateOneAsync(
        HttpClient client,
        string url,
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        var parts = new JsonArray();

        // Reference images precede the instruction, which is the ordering Gemini's own examples
        // use for editing.
        foreach (var reference in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = reference.MediaType,
                    ["data"] = Convert.ToBase64String(reference.Bytes),
                },
            });
        }

        parts.Add((JsonNode)new JsonObject { ["text"] = request.Prompt });

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["parts"] = parts }),
            // Without this the model may answer with prose instead of a picture.
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("IMAGE", "TEXT"),
            },
        };

        if (request.Seed is { } seed)
        {
            ((JsonObject)body["generationConfig"]!)["seed"] = seed;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Gemini image generation failed with {StatusCode} for model {Model}",
                    (int)response.StatusCode,
                    request.ModelKey);

                return ImageGenerationResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            return ImageGenerationResult.Success(ParseImages(text));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ImageGenerationResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return ImageGenerationResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return ImageGenerationResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }

    private static List<GeneratedImage> ParseImages(string body)
    {
        using var document = JsonDocument.Parse(body);
        var images = new List<GeneratedImage>();

        if (!document.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            return images;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                // camelCase from the REST API, snake_case from some proxies — accept both
                // rather than silently returning nothing against a relay.
                if (!TryGetInlineData(part, out var inline)) continue;

                var data = ReadString(inline, "data");
                if (string.IsNullOrEmpty(data)) continue;

                var mediaType = ReadString(inline, "mimeType")
                    ?? ReadString(inline, "mime_type")
                    ?? "image/png";

                try
                {
                    images.Add(new GeneratedImage(Convert.FromBase64String(data), mediaType));
                }
                catch (FormatException)
                {
                    // Skip the malformed part, keep the rest.
                }
            }
        }

        return images;
    }

    private static bool TryGetInlineData(JsonElement part, out JsonElement inline) =>
        part.TryGetProperty("inlineData", out inline)
        || part.TryGetProperty("inline_data", out inline);

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
