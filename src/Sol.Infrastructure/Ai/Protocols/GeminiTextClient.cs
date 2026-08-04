using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// Gemini text via <c>generateContent</c>.
/// </summary>
/// <remarks>
/// The same endpoint <see cref="GeminiImagesClient"/> uses — Gemini has no separate chat surface,
/// only a different response modality. Needed because a chat-category model may legitimately
/// resolve to the <c>gemini</c> protocol, which would otherwise dispatch to nothing.
/// </remarks>
internal sealed class GeminiTextClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GeminiTextClient> logger) : ITextGenerationClient
{
    public ProviderType Protocol => ProviderType.Gemini;

    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var parts = new JsonArray();

        foreach (var image in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        parts.Add((JsonNode)new JsonObject { ["text"] = request.Prompt });

        var generationConfig = new JsonObject
        {
            // Text only: the image client sets IMAGE here, and leaving it unset lets an
            // image-capable model answer a text request with a picture.
            ["responseModalities"] = new JsonArray("TEXT"),
        };

        if (request.MaxOutputTokens is { } maxTokens)
        {
            generationConfig["maxOutputTokens"] = maxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = generationConfig,
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }),
            };
        }

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/v1beta/models/{request.ModelKey}:generateContent",
            body,
            message => message.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey),
            ExtractText,
            ct);
    }

    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

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
                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    builder.Append(text.GetString());
                }
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
