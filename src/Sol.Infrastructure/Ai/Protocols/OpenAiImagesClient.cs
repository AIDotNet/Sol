using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI-compatible image generation (<c>/images/generations</c> and <c>/images/edits</c>).
/// </summary>
/// <remarks>
/// Also serves OpenAI-compatible relays and aggregators, which is why the base URL is taken from
/// the provider rather than hard-coded.
/// <para>
/// Requests are composed with <see cref="JsonObject"/> and responses read with
/// <see cref="JsonDocument"/>. Both are non-reflective and therefore AOT-safe; a DTO per vendor
/// would have to be registered in a serializer context and would break whenever a field moved.
/// </para>
/// </remarks>
internal sealed class OpenAiImagesClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiImagesClient> logger) : IImageGenerationClient
{
    /// <summary>
    /// Cap on one image fetched by URL. The bytes are buffered whole before they are handed to the
    /// asset store, so an unbounded response is a memory amplification vector rather than merely a
    /// slow request. Sized a little above the 20 MB upload limit the API enforces.
    /// </summary>
    private const long MaxDownloadBytes = 25 * 1024 * 1024;

    public ProviderType Protocol => ProviderType.OpenAiImages;

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        // Reference images switch the call to the edits endpoint, which is multipart rather
        // than JSON — a different request shape, not just a different field.
        var isEdit = request.ReferenceImages.Count > 0;

        using var message = isEdit
            ? BuildEditRequest(baseUrl, request)
            : BuildGenerateRequest(baseUrl, request);

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Image generation failed with {StatusCode} for model {Model}",
                    (int)response.StatusCode,
                    request.ModelKey);

                return ImageGenerationResult.Failure(
                    (int)response.StatusCode, ExtractError(body));
            }

            var images = await ReadImagesAsync(client, body, request, ct);

            return images.Count > 0
                ? ImageGenerationResult.Success(images)
                : ImageGenerationResult.Failure(
                    null,
                    "Upstream returned no usable image data. If it replied with links, they may "
                    + "have expired or been unreachable from the server.");
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

    internal static HttpRequestMessage BuildGenerateRequest(
        string baseUrl,
        ImageGenerationRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
            ["n"] = request.Count,
            ["output_format"] = ResolveOutputFormat(request.OutputFormat),
            ["response_format"] = ResolveResponseFormat(request.ResponseFormat),
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            body["size"] = request.Size;
        }

        if (!string.IsNullOrWhiteSpace(request.Quality) && request.Quality != "auto")
        {
            body["quality"] = request.Quality;
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/images/generations")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    internal static HttpRequestMessage BuildEditRequest(
        string baseUrl,
        ImageGenerationRequest request)
    {
        // The edits endpoint takes multipart/form-data, and MultipartFormDataContent must not be
        // disposed before the request is sent — ownership passes to the HttpRequestMessage.
        var content = new MultipartFormDataContent
        {
            { new StringContent(request.ModelKey), "model" },
            { new StringContent(request.Prompt), "prompt" },
            { new StringContent(request.Count.ToString()), "n" },
            { new StringContent(ResolveOutputFormat(request.OutputFormat)), "output_format" },
            { new StringContent(ResolveResponseFormat(request.ResponseFormat)), "response_format" },
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            content.Add(new StringContent(request.Size), "size");
        }

        // Editing honours quality the same way generating does. Omitting it here meant the
        // control went dead the moment a reference image was wired in.
        if (!string.IsNullOrWhiteSpace(request.Quality) && request.Quality != "auto")
        {
            content.Add(new StringContent(request.Quality), "quality");
        }

        for (var i = 0; i < request.ReferenceImages.Count; i++)
        {
            var reference = request.ReferenceImages[i];
            var part = new ByteArrayContent(reference.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(reference.MediaType);

            // "image[]" is how the API accepts several reference images; a single one may also
            // be sent as "image", and the array form is accepted in both cases.
            content.Add(part, "image[]", $"reference-{i}{ExtensionFor(reference.MediaType)}");
        }

        return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/images/edits")
        {
            Content = content,
        };
    }

    /// <summary>Normalizes the requested encoding to a value the API accepts.</summary>
    /// <remarks>
    /// Whitelisted rather than passed through so an unrecognized value degrades to the API's own
    /// default instead of earning a 400 that reads as a broken model.
    /// </remarks>
    internal static string ResolveOutputFormat(string? requested) =>
        requested?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "jpeg",
            "webp" => "webp",
            _ => "png",
        };

    /// <summary>
    /// Maps how the caller wants the bytes delivered onto what the API calls it.
    /// </summary>
    /// <remarks>
    /// The application says <c>base64</c>; the API spells it <c>b64_json</c>. Defaults to
    /// <c>url</c>, which is what most OpenAI-compatible relays return and the cheaper response to
    /// move around — the bytes are fetched server-side either way.
    /// </remarks>
    internal static string ResolveResponseFormat(string? requested) =>
        requested?.Trim().ToLowerInvariant() switch
        {
            "base64" or "b64_json" or "b64json" => "b64_json",
            _ => "url",
        };

    private static string MediaTypeFor(string outputFormat) => outputFormat switch
    {
        "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>
    /// Reads generated images out of the response.
    /// </summary>
    /// <remarks>
    /// An entry carries either the bytes inline (<c>b64_json</c>) or a link to them (<c>url</c>),
    /// depending on what was asked for and what the provider honoured — so both are handled, and a
    /// link is followed here rather than handed onward. The canvas serves images from our own asset
    /// route, which needs the caller's device cookie, and a provider's link is short-lived: if the
    /// bytes are not pulled down now they are not retrievable later.
    /// </remarks>
    private async Task<List<GeneratedImage>> ReadImagesAsync(
        HttpClient client,
        string body,
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // What the response should be, for the entries that do not say. A URL download reports its
        // own content type and is trusted over this.
        var expected = MediaTypeFor(ResolveOutputFormat(request.OutputFormat));

        var images = new List<GeneratedImage>(data.GetArrayLength());

        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            if (ReadString(element, "b64_json") is { Length: > 0 } encoded)
            {
                if (Decode(encoded, expected) is { } inline) images.Add(inline);
                continue;
            }

            if (ReadString(element, "url") is not { Length: > 0 } url) continue;

            // Some relays answer a url request with the bytes inline as a data URL.
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadDataUrl(url, expected) is { } embedded) images.Add(embedded);
                continue;
            }

            if (await DownloadAsync(client, url, request, expected, ct) is { } downloaded)
            {
                images.Add(downloaded);
            }
        }

        return images;
    }

    /// <summary>Fetches an image the provider returned by reference.</summary>
    private async Task<GeneratedImage?> DownloadAsync(
        HttpClient client,
        string url,
        ImageGenerationRequest request,
        string expectedMediaType,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);

            // The key travels only to the provider's own host. Vendors that hand back a pre-signed
            // storage link reject an Authorization header outright, and sending it would leak the
            // key to a third party; a relay serving the image from its own API host needs it.
            if (IsProviderHost(uri, request.Provider.BaseUrl))
            {
                message.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", request.ApiKey);
            }

            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Downloading a generated image failed with {StatusCode}",
                    (int)response.StatusCode);

                return null;
            }

            // Checked before reading so an oversized image costs a header round-trip, not the
            // memory. Re-checked after, since Content-Length is optional.
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                logger.LogInformation("A generated image exceeded the download cap and was skipped.");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            if (bytes.Length == 0 || bytes.Length > MaxDownloadBytes)
            {
                return null;
            }

            var reported = response.Content.Headers.ContentType?.MediaType;

            // A provider that serves images from a plain file host may report
            // application/octet-stream; the format that was asked for is the better guess.
            return new GeneratedImage(
                bytes,
                reported is { Length: > 0 }
                && reported.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? reported
                    : expectedMediaType);
        }
        catch (HttpRequestException exception)
        {
            // One image that will not come down should not discard the ones that did.
            logger.LogWarning(exception, "Downloading a generated image failed.");
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Downloading a generated image timed out.");
            return null;
        }
    }

    private static bool IsProviderHost(Uri uri, string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var provider)
        && string.Equals(uri.Host, provider.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the payload out of a <c>data:</c> URL, e.g. <c>data:image/png;base64,iVBOR…</c>.</summary>
    private static GeneratedImage? ReadDataUrl(string url, string expectedMediaType)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return null;

        // Everything between "data:" and the comma: the media type, then any parameters.
        var header = url.AsSpan(5, comma - 5);

        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;

        var semicolon = header.IndexOf(';');
        var declared = semicolon >= 0 ? header[..semicolon].Trim().ToString() : string.Empty;

        return Decode(
            url[(comma + 1)..],
            declared.Length > 0 ? declared : expectedMediaType);
    }

    private static GeneratedImage? Decode(string base64, string mediaType)
    {
        try
        {
            return new GeneratedImage(Convert.FromBase64String(base64), mediaType);
        }
        catch (FormatException)
        {
            // One malformed entry should not discard the images that did decode.
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Pulls the human-readable message out of an error body, falling back to the raw text.</summary>
    internal static string ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? body;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — an HTML error page from a proxy, most likely.
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    internal static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };
}
