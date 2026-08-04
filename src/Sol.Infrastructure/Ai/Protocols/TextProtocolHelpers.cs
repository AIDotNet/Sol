using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// The request/response plumbing the text clients share.
/// </summary>
/// <remarks>
/// Each protocol differs only in its URL, auth header and response shape. Centralising the rest
/// keeps the failure handling identical across them — an unreachable host, a timeout and a
/// malformed body should read the same to the user whichever vendor produced them.
/// </remarks>
internal static class TextProtocolHelpers
{
    public static async Task<TextGenerationResult> SendAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string url,
        JsonObject body,
        Action<HttpRequestMessage> authorize,
        Func<JsonElement, string?> extractText,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        authorize(message);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Text generation failed with {StatusCode} at {Url}", (int)response.StatusCode, url);

                return TextGenerationResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var generated = extractText(document.RootElement);

            // A 200 with nothing usable in it usually means a content filter or a refusal that
            // the vendor chose not to express as an error status.
            return string.IsNullOrWhiteSpace(generated)
                ? TextGenerationResult.Failure(null, "Upstream returned no text.")
                : TextGenerationResult.Success(generated);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TextGenerationResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return TextGenerationResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return TextGenerationResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }
}
