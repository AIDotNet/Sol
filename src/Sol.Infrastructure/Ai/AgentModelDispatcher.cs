using System.Runtime.CompilerServices;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

internal sealed class AgentModelDispatcher : IAgentModelDispatcher
{
    private readonly Dictionary<ProviderType, IAgentModelClient> _clients;

    public AgentModelDispatcher(IEnumerable<IAgentModelClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        ProviderType protocol,
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            yield return new AgentModelEvent(
                AgentModelEventKind.Failed,
                Error: $"No agent client is registered for protocol '{protocol.ToWire()}'.");
            yield break;
        }

        await foreach (var item in client.StreamAsync(request, ct))
        {
            yield return item;
        }
    }
}
