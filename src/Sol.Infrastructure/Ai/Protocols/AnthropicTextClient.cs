using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// Anthropic Messages (<c>POST /v1/messages</c>).
/// </summary>
/// <remarks>
/// Differs from the OpenAI shape in three ways that matter here: the system prompt is a
/// top-level field rather than a message, <c>max_tokens</c> is required, and images are
/// <c>source</c> objects carrying base64 rather than data URLs.
/// </remarks>
internal sealed class AnthropicTextClient(
    IHttpClientFactory httpClientFactory,
    ILogger<AnthropicTextClient> logger) : ITextGenerationClient
{
    /// <summary>
    /// Anthropic rejects a request without <c>max_tokens</c>, so a caller that does not care
    /// still needs a value. Generous enough for a rewritten prompt.
    /// </summary>
    private const int DefaultMaxTokens = 4096;

    public ProviderType Protocol => ProviderType.Anthropic;

    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        // Base URLs are normalized to drop a trailing /v1, so it is re-added here.
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        var parts = new JsonArray();
        foreach (var image in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = request.Prompt });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["max_tokens"] = request.MaxOutputTokens ?? DefaultMaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = parts }),
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["system"] = request.SystemPrompt;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/v1/messages",
            body,
            message =>
            {
                message.Headers.TryAddWithoutValidation("x-api-key", request.ApiKey);
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            },
            ExtractText,
            ct);
    }

    /// <summary>
    /// Concatenates the text blocks of the response.
    /// </summary>
    /// <remarks>
    /// Content is always an array of typed blocks, and a thinking-enabled model interleaves
    /// <c>thinking</c> blocks with <c>text</c> ones — only the latter belong in the output.
    /// </remarks>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
