using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI-style video generation (Sora).
/// </summary>
/// <remarks>
/// A much narrower parameter set than Seedance: duration, aspect and resolution only. Sending
/// fps, seed or watermark is rejected, so those are dropped here rather than passed through —
/// the node UI already hides them, and this is the backstop.
/// </remarks>
internal sealed class OpenAiVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiVideoClient> logger) : IVideoGenerationClient
{
    public ProviderType Protocol => ProviderType.OpenAiVideo;

    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        var body = BuildRequestBody(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/videos")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Video submit failed with {StatusCode}", (int)response.StatusCode);
                return VideoSubmitResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var id = document.RootElement.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

            return string.IsNullOrEmpty(id)
                ? VideoSubmitResult.Failure(null, "Upstream returned no job id.")
                : VideoSubmitResult.Success(id);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return VideoSubmitResult.Failure(null, exception.Message);
        }
    }

    public async Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = provider.BaseUrl.TrimEnd('/');

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/videos/{upstreamJobId}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Treated as still-running so a blip does not fail a job that is fine; the
                // stale-job sweep bounds how long an unresponsive job can linger.
                return new VideoPollResult(VideoJobState.Running, null, null, null);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            var progress = root.TryGetProperty("progress", out var progressElement)
                && progressElement.ValueKind == JsonValueKind.Number
                    ? progressElement.GetDouble() / 100
                    : (double?)null;

            return status switch
            {
                // The content is fetched from a sub-resource rather than returned inline.
                "completed" => new VideoPollResult(
                    VideoJobState.Succeeded, 1, $"{baseUrl}/videos/{upstreamJobId}/content", null),
                "failed" or "cancelled" => new VideoPollResult(
                    VideoJobState.Failed, null, null, ReadError(root) ?? "Generation failed."),
                "in_progress" or "processing" => new VideoPollResult(
                    VideoJobState.Running, progress, null, null),
                _ => new VideoPollResult(VideoJobState.Pending, progress, null, null),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new VideoPollResult(VideoJobState.Running, null, null, null);
        }
    }

    internal static JsonObject BuildRequestBody(VideoGenerationRequest request)
    {
        if (request.Provider.BuiltinId == "routin-ai")
        {
            var content = new JsonArray();
            foreach (var image in request.ReferenceImages)
            {
                // These models fetch their references over http(s) and reject data: URLs outright,
                // so a public asset link is sent when the deployment can publish one; the inline
                // base64 remains for deployments without a reachable public origin.
                var url = image.Url
                    ?? $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";
                content.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image_url",
                    ["role"] = "reference_image",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = url,
                    },
                });
            }

            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                content.Add((JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.Prompt,
                });
            }

            var routinBody = new JsonObject
            {
                ["model"] = request.ModelKey,
                ["prompt"] = request.Prompt,
                ["content"] = content,
            };

            if (request.Aspect is not null) routinBody["ratio"] = request.Aspect;
            var resolution = FixedResolution(request.ModelKey) ?? request.Resolution;
            if (resolution is not null) routinBody["resolution"] = resolution;
            if (request.DurationSeconds is { } duration) routinBody["duration"] = duration;

            return routinBody;
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
        };

        if (request.DurationSeconds is { } seconds) body["seconds"] = seconds.ToString();
        if (SizeFor(request.ModelKey, request.Aspect, request.Resolution) is { } size)
        {
            body["size"] = size;
        }

        return body;
    }

    /// <summary>
    /// Maps an aspect ratio and resolution to the discrete sizes the API accepts.
    /// </summary>
    /// <remarks>
    /// Sora takes a pixel size, not a ratio, and rejects anything outside its supported set.
    /// </remarks>
    internal static string? SizeFor(string modelKey, string? aspect, string? resolution)
    {
        if (aspect is null) return null;

        var tall = aspect is "9:16" or "3:4" or "2:3";
        var fixedResolution = FixedResolution(modelKey);

        return (fixedResolution ?? resolution) switch
        {
            "1080p" => tall ? "1080x1920" : "1920x1080",
            "480p" => tall ? "480x854" : "854x480",
            _ => tall ? "720x1280" : "1280x720",
        };
    }

    private static string? FixedResolution(string modelKey)
    {
        var normalized = modelKey.Trim().ToLowerInvariant();
        if (!normalized.StartsWith("sd-mini-", StringComparison.Ordinal)
            && !normalized.StartsWith("sd-fast-", StringComparison.Ordinal)
            && !normalized.StartsWith("sd-2.0-", StringComparison.Ordinal))
        {
            return null;
        }

        return normalized.EndsWith("-1080p", StringComparison.Ordinal) ? "1080p"
            : normalized.EndsWith("-720p", StringComparison.Ordinal) ? "720p"
            : normalized.EndsWith("-480p", StringComparison.Ordinal) ? "480p"
            : null;
    }

    private static string? ReadError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
}

/// <summary>
/// xAI video generation (Grok Imagine), per the xAI Videos API also served by the Routin gateway.
/// </summary>
/// <remarks>
/// Deceptively OpenAI-like but a different dialect: creation posts to <c>/videos/generations</c>
/// and returns <c>request_id</c> rather than <c>id</c>, the poll recognizes
/// <c>pending / done / failed / expired</c>, and the finished video arrives inline as
/// <c>video.url</c> instead of behind a <c>/content</c> sub-resource. Parameters are
/// <c>duration</c> (seconds), <c>aspect_ratio</c> and <c>resolution</c>; the node UI additionally
/// caps xAI at 720p.
/// </remarks>
internal sealed class XaiVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<XaiVideoClient> logger) : IVideoGenerationClient
{
    /// <summary>Documented clip bounds: 15s, or 10s when reference images steer the subject.</summary>
    private const int MaxDurationSeconds = 15;
    private const int MaxReferenceDurationSeconds = 10;

    /// <summary>The API takes at most 7 reference images.</summary>
    private const int MaxReferenceImages = 7;

    public ProviderType Protocol => ProviderType.XaiVideo;

    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = ResolveBaseUrl(request.Provider);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/videos/generations")
        {
            Content = new StringContent(
                BuildRequestBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "xAI video submit failed with {StatusCode}", (int)response.StatusCode);
                return VideoSubmitResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var id = root.TryGetProperty("request_id", out var requestId)
                ? requestId.GetString()
                : root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

            return string.IsNullOrEmpty(id)
                ? VideoSubmitResult.Failure(null, "Upstream returned no request id.")
                : VideoSubmitResult.Success(id);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return VideoSubmitResult.Failure(null, exception.Message);
        }
    }

    public async Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = ResolveBaseUrl(provider);

        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/videos/{upstreamJobId}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Treated as still-running so a blip does not fail a job that is fine; the
                // stale-job sweep bounds how long an unresponsive job can linger.
                return new VideoPollResult(VideoJobState.Running, null, null, null);
            }

            return ParsePoll(text);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new VideoPollResult(VideoJobState.Running, null, null, null);
        }
    }

    internal static JsonObject BuildRequestBody(VideoGenerationRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
        };

        // image (start frame) and reference_images are mutually exclusive; one connected image is
        // the common image-to-video case and maps to the start frame.
        if (request.ReferenceImages.Count == 1)
        {
            body["image"] = new JsonObject { ["url"] = ImageUrl(request.ReferenceImages[0]) };
        }
        else if (request.ReferenceImages.Count > 1)
        {
            var references = new JsonArray();
            foreach (var image in request.ReferenceImages.Take(MaxReferenceImages))
            {
                references.Add((JsonNode)new JsonObject { ["url"] = ImageUrl(image) });
            }

            body["reference_images"] = references;
        }

        if (request.DurationSeconds is { } duration)
        {
            var max = request.ReferenceImages.Count > 1
                ? MaxReferenceDurationSeconds
                : MaxDurationSeconds;
            body["duration"] = Math.Clamp(duration, 1, max);
        }

        if (request.Aspect is { } aspect) body["aspect_ratio"] = aspect;

        // 1080p is not offered to xAI in the node UI; dropping it here is the backstop.
        if (request.Resolution is "480p" or "720p") body["resolution"] = request.Resolution;

        return body;
    }

    /// <summary>
    /// Maps a poll response body onto a job state.
    /// </summary>
    /// <remarks>
    /// Terminal on <c>done</c>, <c>failed</c> and <c>expired</c>; anything else stays pending so
    /// an unknown status cannot fail a job that is merely new.
    /// </remarks>
    internal static VideoPollResult ParsePoll(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;

        var progress = root.TryGetProperty("progress", out var progressElement)
            && progressElement.ValueKind == JsonValueKind.Number
                ? progressElement.GetDouble() / 100
                : (double?)null;

        return status switch
        {
            // The finished video arrives inline as video.url; there is no content sub-resource.
            "done" => ReadVideoUrl(root) is { } url
                ? new VideoPollResult(VideoJobState.Succeeded, 1, url, null)
                : new VideoPollResult(VideoJobState.Failed, null, null,
                    "Generation finished but no video URL was returned."),
            "failed" => new VideoPollResult(
                VideoJobState.Failed, null, null, ReadError(root) ?? "Generation failed."),
            "expired" => new VideoPollResult(
                VideoJobState.Failed, null, null,
                "The video request expired upstream; submit it again."),
            _ => new VideoPollResult(VideoJobState.Pending, progress, null, null),
        };
    }

    /// <summary>
    /// Resolves the API root for a provider.
    /// </summary>
    /// <remarks>
    /// The Routin gateway mounts the xAI API under <c>/xai/v1</c> while every other protocol it
    /// proxies shares the provider's own root, and a provider row carries a single base URL — so
    /// the xAI root is derived from the origin. Any other host is used verbatim.
    /// </remarks>
    internal static string ResolveBaseUrl(AiProvider provider)
    {
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        if (provider.BuiltinId != "routin-ai"
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return baseUrl;
        }

        return $"{uri.GetLeftPart(UriPartial.Authority)}/xai/v1";
    }

    private static string ImageUrl(ReferenceImage image) =>
        // The API accepts a public URL or an inline base64 data URI; the public asset link wins
        // when the deployment can publish one, keeping large frames out of the request body.
        image.Url ?? $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";

    private static string? ReadVideoUrl(JsonElement root) =>
        root.TryGetProperty("video", out var video)
        && video.TryGetProperty("url", out var url)
        && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;

    private static string? ReadError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
}
