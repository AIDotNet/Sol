using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

internal sealed class VideoGenerationDispatcher : IVideoGenerationDispatcher
{
    private readonly Dictionary<ProviderType, IVideoGenerationClient> _clients;

    public VideoGenerationDispatcher(IEnumerable<IVideoGenerationClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    public Task<VideoSubmitResult> SubmitAsync(
        ProviderType protocol,
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            return Task.FromResult(VideoSubmitResult.Failure(
                null,
                $"No video generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.SubmitAsync(request, ct);
    }

    public Task<VideoPollResult> PollAsync(
        ProviderType protocol,
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            return Task.FromResult(new VideoPollResult(
                VideoJobState.Failed,
                null,
                null,
                $"No video generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.PollAsync(provider, apiKey, upstreamJobId, ct);
    }
}
