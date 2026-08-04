using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI Responses (<c>POST /responses</c>).
/// </summary>
/// <remarks>
/// The newer OpenAI surface. Its differences from chat completions are all in naming:
/// <c>input</c> rather than <c>messages</c>, <c>instructions</c> rather than a system message,
/// <c>max_output_tokens</c>, and <c>input_text</c>/<c>input_image</c> part types. The response
/// is a flat <c>output</c> array that can carry reasoning items alongside the message.
/// </remarks>
internal sealed class OpenAiResponsesClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiResponsesClient> logger) : ITextGenerationClient
{
    public ProviderType Protocol => ProviderType.OpenAiResponses;

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
                ["type"] = "input_image",
                ["image_url"] =
                    $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}",
            });
        }

        parts.Add((JsonNode)new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = request.Prompt,
        });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["input"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = parts }),
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["instructions"] = request.SystemPrompt;
        }

        if (request.MaxOutputTokens is { } maxTokens)
        {
            body["max_output_tokens"] = maxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/responses",
            body,
            message =>
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
            ExtractText,
            ct);
    }

    private static string? ExtractText(JsonElement root)
    {
        // Some deployments provide the flattened convenience field; prefer it when present.
        if (root.TryGetProperty("output_text", out var flattened)
            && flattened.ValueKind == JsonValueKind.String)
        {
            return flattened.GetString();
        }

        if (!root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            // Reasoning items sit in the same array and carry no user-facing text.
            if (!item.TryGetProperty("type", out var itemType)
                || itemType.ValueKind != JsonValueKind.String
                || itemType.GetString() != "message")
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
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
