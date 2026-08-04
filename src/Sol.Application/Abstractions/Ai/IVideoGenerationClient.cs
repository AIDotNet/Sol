using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Starts and polls a video generation job.
/// </summary>
/// <remarks>
/// Video is asynchronous everywhere: the vendor accepts a request, returns a job id, and renders
/// for minutes. That shape is reflected here rather than hidden behind a blocking call, because
/// a request that outlives an HTTP timeout cannot be modelled as one.
/// </remarks>
public interface IVideoGenerationClient
{
    ProviderType Protocol { get; }

    Task<VideoSubmitResult> SubmitAsync(VideoGenerationRequest request, CancellationToken ct);

    Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct);
}

public sealed record VideoGenerationRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    IReadOnlyList<ReferenceImage> ReferenceImages,
    string? Aspect,
    string? Resolution,
    int? DurationSeconds,
    int? Fps,
    int? Seed,
    bool? Watermark,
    bool? GenerateAudio);

public sealed record VideoSubmitResult(bool Ok, string? UpstreamJobId, int? StatusCode, string? Error)
{
    public static VideoSubmitResult Success(string jobId) => new(true, jobId, 200, null);

    public static VideoSubmitResult Failure(int? statusCode, string error) =>
        new(false, null, statusCode, error);
}

public enum VideoJobState
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <param name="VideoUrl">
/// Set only when <see cref="State"/> is <see cref="VideoJobState.Succeeded"/>. Vendors return a
/// short-lived download URL rather than bytes, so the caller must fetch and store it promptly.
/// </param>
public sealed record VideoPollResult(
    VideoJobState State,
    double? Progress,
    string? VideoUrl,
    string? Error);

/// <summary>Routes to the client that speaks the resolved protocol.</summary>
public interface IVideoGenerationDispatcher
{
    Task<VideoSubmitResult> SubmitAsync(
        ProviderType protocol,
        VideoGenerationRequest request,
        CancellationToken ct);

    Task<VideoPollResult> PollAsync(
        ProviderType protocol,
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct);
}
