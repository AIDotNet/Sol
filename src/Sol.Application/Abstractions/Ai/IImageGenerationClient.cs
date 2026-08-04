using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Generates images through one provider protocol.
/// </summary>
/// <remarks>
/// One implementation per <see cref="ProviderType"/>; <c>IImageGenerationDispatcher</c> picks
/// between them using the resolved protocol. Adding a vendor means adding an implementation, not
/// editing a switch in the endpoint.
/// </remarks>
public interface IImageGenerationClient
{
    /// <summary>Protocols this client handles.</summary>
    ProviderType Protocol { get; }

    Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct);
}

/// <param name="ReferenceImages">
/// Existing images to condition on. Passed as raw bytes rather than URLs: the upstream cannot
/// reach our asset endpoint, which requires the caller's device cookie.
/// </param>
/// <param name="Count">How many images to produce. Clamped by the caller to 1-4.</param>
/// <param name="OutputFormat">
/// Encoding to ask the provider for — <c>png</c>, <c>jpeg</c> or <c>webp</c>. Left unnormalized
/// here: which spellings are legal is the protocol's business, and a protocol with no equivalent
/// ignores it.
/// </param>
/// <param name="ResponseFormat">
/// How the images should come back: <c>url</c> or <c>base64</c>. Application vocabulary — the
/// protocol client maps it to whatever the vendor calls it.
/// </param>
public sealed record ImageGenerationRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    IReadOnlyList<ReferenceImage> ReferenceImages,
    string? Size,
    string? Quality,
    string? OutputFormat,
    string? ResponseFormat,
    int Count,
    int? Seed);

public sealed record ReferenceImage(byte[] Bytes, string MediaType);

public sealed record GeneratedImage(byte[] Bytes, string MediaType);

public sealed record ImageGenerationResult(
    bool Ok,
    IReadOnlyList<GeneratedImage> Images,
    int? StatusCode,
    string? Error)
{
    public static ImageGenerationResult Success(IReadOnlyList<GeneratedImage> images) =>
        new(true, images, 200, null);

    public static ImageGenerationResult Failure(int? statusCode, string error) =>
        new(false, [], statusCode, error);
}

/// <summary>Routes a request to the client that speaks the resolved protocol.</summary>
public interface IImageGenerationDispatcher
{
    Task<ImageGenerationResult> GenerateAsync(
        ProviderType protocol,
        ImageGenerationRequest request,
        CancellationToken ct);
}
