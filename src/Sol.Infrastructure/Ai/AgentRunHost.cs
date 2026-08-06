using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Realtime;
using Sol.Application.Features.Agent;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

/// <summary>Runs queued Agent turns outside the HTTP request that created them.</summary>
/// <remarks>
/// Single-instance assumption: in-process cancellation and execution ownership are not shared
/// across replicas. Before enabling more than one API instance, queued-run claiming and live
/// cancellation must move to a distributed lease, just as the video poller requires row locking.
/// </remarks>
internal sealed class AgentRunHost(
    IServiceScopeFactory scopeFactory,
    IAgentCanvasBridge canvasBridge,
    ILogger<AgentRunHost> logger) : BackgroundService, IAgentRunControl
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 8;
    private const int MaxConcurrentRuns = 4;

    private readonly Channel<AgentRunId> _queue = Channel.CreateBounded<AgentRunId>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRuns, MaxConcurrentRuns);

    public void Enqueue(AgentRunId runId) => _queue.Writer.TryWrite(runId);

    public bool Cancel(AgentRunId runId) =>
        _active.TryGetValue(runId.Value, out var source) && Cancel(source);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InterruptOrphansAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
                await DrainAsync(stoppingToken);
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Agent run host tick failed; continuing.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _active.Values) Cancel(source);
        await base.StopAsync(cancellationToken);
    }

    private async Task InterruptOrphansAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var count = await repository.InterruptOrphanedRunsAsync(DateTimeOffset.UtcNow, ct);
        if (count > 0)
        {
            logger.LogWarning("Interrupted {Count} Agent runs left active by the previous process.", count);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        foreach (var runId in await repository.ListQueuedAsync(BatchSize, ct))
        {
            _queue.Writer.TryWrite(runId);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (_queue.Reader.TryRead(out var runId))
        {
            if (_active.ContainsKey(runId.Value)) continue;
            await _concurrency.WaitAsync(ct);
            _ = RunOneAsync(runId, ct);
        }
    }

    private async Task RunOneAsync(AgentRunId runId, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        linked.CancelAfter(TimeSpan.FromMinutes(15));
        if (!_active.TryAdd(runId.Value, linked))
        {
            _concurrency.Release();
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<AgentTurnRunner>();
            try
            {
                await runner.RunAsync(runId, linked.Token);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Agent run {RunId} failed.", runId);
                // Use the host token: a user cancellation has already cancelled the run token, but
                // the terminal state still has to be persisted.
                await runner.FailUnhandledAsync(runId, exception, stoppingToken);
            }
        }
        finally
        {
            canvasBridge.Release(runId);
            _active.TryRemove(runId.Value, out _);
            _concurrency.Release();
        }
    }

    private static bool Cancel(CancellationTokenSource source)
    {
        if (source.IsCancellationRequested) return false;
        source.Cancel();
        return true;
    }
}
