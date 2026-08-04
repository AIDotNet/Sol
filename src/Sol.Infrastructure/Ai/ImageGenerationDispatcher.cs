using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai;

/// <summary>
/// Routes a generation request to the client that speaks the resolved protocol.
/// </summary>
/// <remarks>
/// Built from the registered <see cref="IImageGenerationClient"/> implementations rather than a
/// hard-coded switch, so supporting a new vendor is a registration rather than an edit here.
/// The lookup is a dictionary built once at construction — the alternative, scanning the list per
/// request, is the kind of thing that quietly costs on a hot path.
/// </remarks>
internal sealed class ImageGenerationDispatcher : IImageGenerationDispatcher
{
    private readonly Dictionary<ProviderType, IImageGenerationClient> _clients;

    public ImageGenerationDispatcher(IEnumerable<IImageGenerationClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    public Task<ImageGenerationResult> GenerateAsync(
        ProviderType protocol,
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            // Reachable when a model's protocol override names something that cannot generate
            // images — e.g. a chat protocol on an image model. Better a clear message than a
            // request shaped for the wrong API.
            return Task.FromResult(ImageGenerationResult.Failure(
                null,
                $"No image generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.GenerateAsync(request, ct);
    }
}
