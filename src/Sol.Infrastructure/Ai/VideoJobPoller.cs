using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

/// <summary>
/// Advances in-flight video jobs.
/// </summary>
/// <remarks>
/// Polling happens server-side rather than in the browser so a job survives the tab that started
/// it: the user can close the page, come back, and find the video waiting. The client polls our
/// own row, which is cheap, instead of holding a connection open for minutes.
/// <para>
/// Single-instance assumption: with several API instances every one of them would poll the same
/// jobs. Making this safe to scale out needs a row lock (<c>FOR UPDATE SKIP LOCKED</c>) or a
/// leader election, neither of which is warranted before the deployment actually has replicas.
/// </para>
/// </remarks>
internal sealed class VideoJobPoller(
    IServiceScopeFactory scopeFactory,
    ILogger<VideoJobPoller> logger) : BackgroundService
{
    /// <summary>Vendors render for minutes, so a tight loop only wastes quota.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A job untouched for this long is failed. Bounds a vendor that accepts a job and never
    /// reports on it again, which would otherwise leave a row polling forever.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>How many jobs to advance per tick. Keeps one busy device from starving others.</summary>
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never let one bad tick kill the loop: the jobs it was advancing would hang
                // until the process restarts.
                logger.LogError(exception, "Video job poll failed; continuing.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IVideoJobRepository>();

        var failed = await jobs.FailStaleAsync(DateTimeOffset.UtcNow - StaleAfter, ct);
        if (failed > 0)
        {
            logger.LogWarning("Failed {Count} stale video jobs.", failed);
        }

        var active = await jobs.ListActiveAsync(BatchSize, ct);
        if (active.Count == 0)
        {
            return;
        }

        var providers = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<IApiKeyProtector>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IVideoGenerationDispatcher>();
        var assetStore = scope.ServiceProvider.GetRequiredService<IAssetStore>();
        var assets = scope.ServiceProvider.GetRequiredService<ICanvasAssetRepository>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        foreach (var job in active)
        {
            await AdvanceAsync(
                job, jobs, providers, protector, dispatcher, assetStore, assets,
                httpClientFactory, ct);
        }
    }

    private async Task AdvanceAsync(
        VideoJob job,
        IVideoJobRepository jobs,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IVideoGenerationDispatcher dispatcher,
        IAssetStore assetStore,
        ICanvasAssetRepository assets,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        if (job.UpstreamJobId is null || job.ProviderId is null)
        {
            await FailAsync(jobs, job, "The job lost its provider association.", ct);
            return;
        }

        var provider = await providers.FindAsync(
            job.DeviceId, new ProviderId(job.ProviderId.Value), ct);

        if (provider is null)
        {
            await FailAsync(jobs, job, "The provider was deleted while the job was running.", ct);
            return;
        }

        string apiKey;
        try
        {
            apiKey = provider.ApiKey is null ? string.Empty : protector.Unprotect(provider.ApiKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await FailAsync(jobs, job, "The stored API key could not be decrypted.", ct);
            return;
        }

        var model = provider.Models.FirstOrDefault(m => m.ModelKey == job.ModelKey);
        var protocol = provider.ResolveProtocol(model);

        var result = await dispatcher.PollAsync(
            protocol, provider, apiKey, job.UpstreamJobId, ct);

        switch (result.State)
        {
            case VideoJobState.Succeeded when result.VideoUrl is { } videoUrl:
            {
                // The vendor's URL is short-lived, so the bytes are pulled down now rather than
                // handed to the client to fetch later.
                var stored = await DownloadAsync(
                    httpClientFactory, videoUrl, apiKey, assetStore, ct);

                if (stored is null)
                {
                    await FailAsync(jobs, job, "The finished video could not be downloaded.", ct);
                    return;
                }

                var assetId = Guid.CreateVersion7();
                await assets.InsertAsync(
                    new CanvasAsset(
                        assetId, job.DeviceId, "video", stored.MediaType, stored.StoragePath,
                        stored.ByteSize, null, DateTimeOffset.UtcNow),
                    ct);

                await jobs.UpdateAsync(
                    job with
                    {
                        Status = VideoJobStatus.Succeeded,
                        Progress = 1,
                        AssetId = assetId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    ct);

                logger.LogInformation("Video job {JobId} completed.", job.JobId);
                break;
            }

            case VideoJobState.Failed:
                await FailAsync(jobs, job, result.Error ?? "Generation failed.", ct);
                break;

            default:
                // Still working. The timestamp is refreshed so the stale sweep does not reap a
                // job that is making progress.
                await jobs.UpdateAsync(
                    job with
                    {
                        Status = VideoJobStatus.Running,
                        Progress = result.Progress ?? job.Progress,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    ct);
                break;
        }
    }

    private static async Task FailAsync(
        IVideoJobRepository jobs,
        VideoJob job,
        string error,
        CancellationToken ct)
    {
        await jobs.UpdateAsync(
            job with
            {
                Status = VideoJobStatus.Failed,
                Error = error,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            ct);
    }

    private async Task<StoredAsset?> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        string url,
        string apiKey,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("upstream");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Some vendors return a pre-signed URL that rejects an Authorization header, others
            // return an API route that requires it. Sending it only for same-host URLs keeps
            // both working, and avoids leaking the key to a CDN.
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && parsed.Host.Contains("api.", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Video download returned {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "video/mp4";

            return await assetStore.SaveAsync(bytes, mediaType, ".mp4", ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Video download failed.");
            return null;
        }
    }
}
