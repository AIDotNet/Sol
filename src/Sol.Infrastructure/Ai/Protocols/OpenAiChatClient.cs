using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI-compatible chat completions (<c>POST /chat/completions</c>).
/// </summary>
/// <remarks>
/// The most widely spoken protocol — DeepSeek, SiliconFlow, Volcengine, OpenRouter and most
/// relays all accept this shape, which is why the base URL comes from the provider.
/// </remarks>
internal sealed class OpenAiChatClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiChatClient> logger) : ITextGenerationClient
{
    public ProviderType Protocol => ProviderType.OpenAiChat;

    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add((JsonNode)new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt,
            });
        }

        // A plain string when there are no images: some OpenAI-compatible relays reject the
        // multi-part content array even though the official API accepts it.
        JsonNode content;
        if (request.ReferenceImages.Count == 0)
        {
            content = JsonValue.Create(request.Prompt)!;
        }
        else
        {
            var parts = new JsonArray();
            foreach (var image in request.ReferenceImages)
            {
                parts.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] =
                            $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}",
                    },
                });
            }

            parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
            content = parts;
        }

        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = content });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["messages"] = messages,
        };

        if (request.MaxOutputTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/chat/completions",
            body,
            message =>
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
            ExtractText,
            ct);
    }

    /// <summary>Reads the assistant message out of the first choice.</summary>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var message = choices[0];
        if (!message.TryGetProperty("message", out var inner)) return null;

        if (inner.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        // Reasoning models may return content as an array of typed parts.
        if (text.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in text.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var partText)
                    && partText.ValueKind == JsonValueKind.String)
                {
                    builder.Append(partText.GetString());
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        return null;
    }
}
