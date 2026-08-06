using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;

namespace Sol.Api.Endpoints;

/// <summary>
/// Video generation, which is asynchronous end to end.
/// </summary>
/// <remarks>
/// The POST returns a job id as soon as the vendor accepts the work; a background poller
/// advances it and stores the finished file. The client polls the status route. This is why the
/// job survives a closed tab — nothing about the run depends on the request that started it
/// staying open.
/// </remarks>
public static class AiVideoEndpoints
{
    private const int MaxReferenceImages = 4;
    private const int MinDurationSeconds = 1;
    private const int MaxDurationSeconds = 60;
    private const int DefaultDurationSeconds = 5;

    public static IEndpointRouteBuilder MapAiVideoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/videos", StartAsync).WithTags("ai").WithName("StartVideoJob");
        app.MapGet("/api/v1/ai/videos/{id}", GetStatusAsync)
            .WithTags("ai")
            .WithName("GetVideoJob");
        app.MapDelete("/api/v1/ai/videos/{id}", CancelAsync)
            .WithTags("ai")
            .WithName("CancelVideoJob");

        return app;
    }

    /// <summary>
    /// Marks a job cancelled so the poller stops advancing it.
    /// </summary>
    /// <remarks>
    /// This does not recall the work upstream — most vendors offer no cancel, and the render is
    /// already paid for. What it does is stop this job from consuming polls and from writing a
    /// result the user no longer wants.
    /// <para>
    /// Cancelling an already-finished job is a no-op rather than an error: by the time a user
    /// clicks stop, the job may well have completed, and reporting a failure there would be
    /// confusing.
    /// </para>
    /// </remarks>
    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> CancelAsync(
        string id,
        HttpContext http,
        IVideoJobRepository jobs,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var jobId))
        {
            return TypedResults.NotFound();
        }

        var job = await jobs.FindAsync(deviceId, jobId, ct);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        if (job.Status is VideoJobStatus.Pending or VideoJobStatus.Running)
        {
            await jobs.UpdateAsync(
                job with { Status = VideoJobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow },
                ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<StartVideoResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> StartAsync(
        StartVideoRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IVideoGenerationDispatcher dispatcher,
        IVideoJobRepository jobs,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(request.ProviderId, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt) && (request.Images?.Length ?? 0) == 0)
        {
            return Invalid("a prompt or at least one reference image is required");
        }

        var duration = request.Duration ?? DefaultDurationSeconds;
        if (duration is < MinDurationSeconds or > MaxDurationSeconds)
        {
            return Invalid(
                $"duration must be between {MinDurationSeconds} and {MaxDurationSeconds} seconds");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        if (!provider.Enabled)
        {
            return Invalid($"provider '{provider.Name}' is disabled");
        }

        var model = provider.Models.FirstOrDefault(m => m.ModelKey == request.ModelKey);
        if (model is null)
        {
            return Invalid($"model '{request.ModelKey}' is not configured on this provider");
        }

        if (model.Category != ModelCategory.Video)
        {
            return Invalid($"model '{request.ModelKey}' is not a video model");
        }

        string apiKey;
        try
        {
            apiKey = provider.ApiKey is null ? string.Empty : protector.Unprotect(provider.ApiKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Invalid("The stored API key could not be decrypted. Re-enter it in settings.");
        }

        var references = new List<ReferenceImage>();
        if (request.Images is { Length: > 0 } urls)
        {
            if (urls.Length > MaxReferenceImages)
            {
                return Invalid($"at most {MaxReferenceImages} reference images are allowed");
            }

            foreach (var url in urls)
            {
                // Only our own asset paths resolve; an arbitrary URL is ignored rather than
                // fetched, so this cannot be used as a request-forgery primitive.
                if (await ResolveReferenceAsync(url, deviceId, assets, assetStore, ct) is { } image)
                {
                    references.Add(image);
                }
            }
        }

        var protocol = provider.ResolveProtocol(model);
        var inputMode = request.InputMode?.Trim().ToLowerInvariant();
        if (inputMode is not (null or "reference" or "first-last"))
        {
            return Invalid("inputMode must be 'reference' or 'first-last'");
        }

        if (protocol == ProviderType.SeedanceVideo
            && inputMode == "first-last"
            && references.Count > 2)
        {
            return Invalid("first-last mode accepts at most two connected images");
        }

        var submit = await dispatcher.SubmitAsync(
            protocol,
            new VideoGenerationRequest(
                provider,
                apiKey,
                request.ModelKey,
                request.Prompt,
                references,
                inputMode ?? "reference",
                request.Aspect,
                request.Resolution,
                duration,
                request.Fps,
                request.Seed,
                request.Watermark,
                request.GenerateAudio),
            ct);

        if (!submit.Ok || submit.UpstreamJobId is null)
        {
            return Invalid(submit.Error ?? "the provider rejected the video request");
        }

        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.CreateVersion7();

        // The original request is echoed into the row so a reload can show what was asked for
        // even after the config node has changed.
        var requestJson = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["inputMode"] = inputMode ?? "reference",
            ["aspect"] = request.Aspect,
            ["resolution"] = request.Resolution,
            ["duration"] = duration,
        }.ToJsonString();

        await jobs.InsertAsync(
            new VideoJob(
                jobId,
                deviceId,
                providerId.Value,
                request.ModelKey,
                VideoJobStatus.Running,
                submit.UpstreamJobId,
                null,
                null,
                null,
                requestJson,
                now,
                now),
            ct);

        return TypedResults.Ok(new StartVideoResponse(jobId.ToString()));
    }

    private static async Task<Results<Ok<VideoJobStatusResponse>, NotFound,
        UnauthorizedHttpResult>> GetStatusAsync(
        string id,
        HttpContext http,
        IVideoJobRepository jobs,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var jobId))
        {
            return TypedResults.NotFound();
        }

        var job = await jobs.FindAsync(deviceId, jobId, ct);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new VideoJobStatusResponse(
            job.JobId.ToString(),
            job.Status switch
            {
                VideoJobStatus.Pending => "pending",
                VideoJobStatus.Running => "running",
                VideoJobStatus.Succeeded => "succeeded",
                VideoJobStatus.Failed => "failed",
                _ => "cancelled",
            },
            job.Progress,
            job.AssetId is { } assetId ? $"/api/v1/canvas/assets/{assetId}" : null,
            job.Error));
    }

    private static async Task<ReferenceImage?> ResolveReferenceAsync(
        string url,
        Sol.Domain.Identity.DeviceId deviceId,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        const string prefix = "/api/v1/canvas/assets/";

        if (!url.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(url[prefix.Length..].Split('?')[0], out var assetId))
        {
            return null;
        }

        var asset = await assets.FindAsync(deviceId, assetId, ct);
        if (asset is null) return null;

        await using var stream = assetStore.OpenRead(asset.StoragePath);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        return new ReferenceImage(buffer.ToArray(), asset.MediaType);
    }

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
