using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// Seedance video generation (Volcengine Ark <c>/contents/generations/tasks</c>).
/// </summary>
/// <remarks>
/// Seedance 1.x carries its parameters as <c>--flag value</c> suffixes appended to the prompt
/// text rather than as JSON fields — an unusual convention, but it is what the API accepts.
/// Seedance 2.x moved to structured top-level fields and rejects <c>fps</c> entirely, so the
/// model id decides which shape is sent.
/// </remarks>
internal sealed class SeedanceVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<SeedanceVideoClient> logger) : IVideoGenerationClient
{
    public ProviderType Protocol => ProviderType.SeedanceVideo;

    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var structured = IsStructuredModel(request.ModelKey);

        var content = new JsonArray();

        // Reference images come first; the first is treated as the opening frame.
        foreach (var reference in request.ReferenceImages)
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{reference.MediaType};base64,{Convert.ToBase64String(reference.Bytes)}",
                },
            });
        }

        content.Add((JsonNode)new JsonObject
        {
            ["type"] = "text",
            ["text"] = structured ? request.Prompt : BuildFlaggedPrompt(request),
        });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["content"] = content,
        };

        if (structured)
        {
            // 2.x takes these as real fields. fps is deliberately absent — it is rejected.
            if (request.Resolution is { } resolution) body["resolution"] = resolution;
            if (request.DurationSeconds is { } duration) body["duration"] = duration;
            if (request.Aspect is { } aspect) body["ratio"] = aspect;
            if (request.Seed is { } seed) body["seed"] = seed;
            if (request.Watermark is { } watermark) body["watermark"] = watermark;
            if (request.GenerateAudio is { } audio) body["generate_audio"] = audio;
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/contents/generations/tasks")
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
                    "Seedance submit failed with {StatusCode}", (int)response.StatusCode);
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

        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/contents/generations/tasks/{upstreamJobId}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // A transient upstream error is reported as still-running rather than failed:
                // the poller will retry, and the stale-job sweep bounds how long that can last.
                return new VideoPollResult(VideoJobState.Running, null, null, null);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            return status switch
            {
                "succeeded" => new VideoPollResult(
                    VideoJobState.Succeeded, 1, ReadVideoUrl(root), null),
                "failed" or "cancelled" => new VideoPollResult(
                    VideoJobState.Failed, null, null, ReadError(root) ?? "Generation failed."),
                "running" => new VideoPollResult(VideoJobState.Running, null, null, null),
                _ => new VideoPollResult(VideoJobState.Pending, null, null, null),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new VideoPollResult(VideoJobState.Running, null, null, null);
        }
    }

    /// <summary>
    /// Appends 1.x parameters to the prompt as <c>--flag value</c> pairs.
    /// </summary>
    private static string BuildFlaggedPrompt(VideoGenerationRequest request)
    {
        var builder = new StringBuilder(request.Prompt);

        if (request.Aspect is { } aspect) builder.Append(" --ratio ").Append(aspect);
        if (request.DurationSeconds is { } duration) builder.Append(" --dur ").Append(duration);
        if (request.Fps is { } fps) builder.Append(" --fps ").Append(fps);
        if (request.Resolution is { } resolution) builder.Append(" --rs ").Append(resolution);
        if (request.Seed is { } seed) builder.Append(" --seed ").Append(seed);
        if (request.Watermark is { } watermark)
        {
            builder.Append(" --wm ").Append(watermark ? "true" : "false");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Detects a Seedance 2.x model id, which takes structured fields instead of prompt flags.
    /// </summary>
    private static bool IsStructuredModel(string modelKey) =>
        modelKey.Contains("seedance-2", StringComparison.OrdinalIgnoreCase)
        || modelKey.Contains("seedance-pro-2", StringComparison.OrdinalIgnoreCase);

    private static string? ReadVideoUrl(JsonElement root) =>
        root.TryGetProperty("content", out var content)
        && content.TryGetProperty("video_url", out var url)
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
