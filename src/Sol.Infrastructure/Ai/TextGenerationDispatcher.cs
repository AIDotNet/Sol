using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

internal sealed class TextGenerationDispatcher : ITextGenerationDispatcher
{
    private readonly Dictionary<ProviderType, ITextGenerationClient> _clients;

    public TextGenerationDispatcher(IEnumerable<ITextGenerationClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    public Task<TextGenerationResult> GenerateAsync(
        ProviderType protocol,
        TextGenerationRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            // Reachable when a chat model's protocol override names an image or video protocol.
            return Task.FromResult(TextGenerationResult.Failure(
                null,
                $"No text generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.GenerateAsync(request, ct);
    }
}
