using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI-style video generation (Sora, and xAI which follows the same shape).
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

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
        };

        if (request.DurationSeconds is { } duration)
        {
            body["seconds"] = duration.ToString();
        }

        if (SizeFor(request.Aspect, request.Resolution) is { } size)
        {
            body["size"] = size;
        }

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
                "failed" => new VideoPollResult(
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

    /// <summary>
    /// Maps an aspect ratio and resolution to the discrete sizes the API accepts.
    /// </summary>
    /// <remarks>
    /// Sora takes a pixel size, not a ratio, and rejects anything outside its supported set.
    /// </remarks>
    private static string? SizeFor(string? aspect, string? resolution)
    {
        if (aspect is null) return null;

        var tall = aspect is "9:16" or "3:4" or "2:3";

        return resolution switch
        {
            "1080p" => tall ? "1080x1920" : "1920x1080",
            "480p" => tall ? "480x854" : "854x480",
            _ => tall ? "720x1280" : "1280x720",
        };
    }

    private static string? ReadError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
}

/// <summary>
/// xAI video generation.
/// </summary>
/// <remarks>
/// Wire-compatible with the OpenAI shape, so it reuses that implementation and only differs by
/// the protocol it registers under. The node UI additionally caps xAI at 720p.
/// </remarks>
internal sealed class XaiVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiVideoClient> logger) : IVideoGenerationClient
{
    private readonly OpenAiVideoClient _inner = new(httpClientFactory, logger);

    public ProviderType Protocol => ProviderType.XaiVideo;

    public Task<VideoSubmitResult> SubmitAsync(VideoGenerationRequest request, CancellationToken ct) =>
        _inner.SubmitAsync(request, ct);

    public Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct) =>
        _inner.PollAsync(provider, apiKey, upstreamJobId, ct);
}
