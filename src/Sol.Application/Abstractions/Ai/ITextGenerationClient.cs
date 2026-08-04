using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Generates text through one provider protocol.
/// </summary>
/// <remarks>
/// Non-streaming. The canvas uses text generation for prompt writing and rewriting, where the
/// result is a short block that replaces a node's contents — streaming would add an SSE
/// transport and partial-state handling for no visible benefit at that length.
/// </remarks>
public interface ITextGenerationClient
{
    ProviderType Protocol { get; }

    Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken ct);
}

/// <param name="SystemPrompt">
/// Steering that is not part of the user's own text — used by the rewrite feature to describe
/// the transformation without polluting the prompt it is transforming.
/// </param>
/// <param name="ReferenceImages">
/// Passed to vision-capable models so a prompt can be written from a picture. Ignored by
/// clients whose protocol has no image part.
/// </param>
public sealed record TextGenerationRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    string? SystemPrompt,
    IReadOnlyList<ReferenceImage> ReferenceImages,
    int? MaxOutputTokens,
    double? Temperature);

public sealed record TextGenerationResult(bool Ok, string? Text, int? StatusCode, string? Error)
{
    public static TextGenerationResult Success(string text) => new(true, text, 200, null);

    public static TextGenerationResult Failure(int? statusCode, string error) =>
        new(false, null, statusCode, error);
}

/// <summary>Routes a request to the client that speaks the resolved protocol.</summary>
public interface ITextGenerationDispatcher
{
    Task<TextGenerationResult> GenerateAsync(
        ProviderType protocol,
        TextGenerationRequest request,
        CancellationToken ct);
}
