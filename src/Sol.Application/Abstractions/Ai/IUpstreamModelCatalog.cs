using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Ai;

/// <summary>
/// Queries a provider's own model catalog.
/// </summary>
/// <remarks>
/// The list is advisory. Vendors ship models faster than any preset file tracks, so this is how
/// a user reaches a model the app has never heard of. What comes back carries no category
/// metadata in most protocols, so the implementation infers one from the identifier.
/// </remarks>
public interface IUpstreamModelCatalog
{
    Task<UpstreamCatalogResult> ListModelsAsync(
        AiProvider provider,
        string apiKey,
        CancellationToken ct);
}

/// <param name="Models">Empty when <paramref name="Ok"/> is false.</param>
public sealed record UpstreamCatalogResult(
    bool Ok,
    IReadOnlyList<UpstreamModel> Models,
    int? StatusCode,
    string? Error)
{
    public static UpstreamCatalogResult Success(IReadOnlyList<UpstreamModel> models) =>
        new(true, models, 200, null);

    public static UpstreamCatalogResult Failure(int? statusCode, string error) =>
        new(false, [], statusCode, error);
}

public sealed record UpstreamModel(string ModelKey, string Name, ModelCategory Category);
