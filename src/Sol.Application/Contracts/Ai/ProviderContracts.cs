namespace Sol.Application.Contracts.Ai;

/// <summary>
/// A provider as returned to the browser.
/// </summary>
/// <remarks>
/// There is deliberately no <c>ApiKey</c> field. The stored key is never serialized — callers
/// get <see cref="HasApiKey"/> and <see cref="ApiKeyHint"/>, which is enough to render the
/// settings UI and nothing more. Adding a plaintext field here would leak every key to the
/// browser in a single response.
/// </remarks>
public sealed record ProviderResponse(
    string Id,
    string? BuiltinId,
    string Name,
    string? Description,
    string? Icon,
    string Type,
    string BaseUrl,
    bool Enabled,
    bool HasApiKey,
    string? ApiKeyHint,
    int SortOrder,
    int? PresetVersion,
    string CreatedAt,
    string UpdatedAt,
    ModelResponse[] Models);

/// <param name="Type">Protocol override; null means the model follows its provider's protocol.</param>
public sealed record ModelResponse(
    string Id,
    string ModelKey,
    string Name,
    bool Enabled,
    string? Type,
    string Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool SupportsVision,
    bool SupportsFunctionCall,
    bool SupportsThinking,
    int SortOrder);

public sealed record ProviderListResponse(ProviderResponse[] Providers);

public sealed record CreateProviderRequest(
    string? BuiltinId,
    string Name,
    string? Description,
    string? Icon,
    string Type,
    string BaseUrl,
    string? ApiKey,
    bool? Enabled,
    int? PresetVersion,
    CreateModelRequest[]? Models);

/// <summary>
/// A partial provider update. Every field is optional; null means "leave unchanged".
/// </summary>
/// <param name="ApiKey">
/// Null leaves the stored key alone — the common case, since most edits touch a base URL or a
/// name. An empty string clears it. Anything else replaces it.
/// </param>
public sealed record UpdateProviderRequest(
    string? Name,
    string? Description,
    string? Icon,
    string? Type,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled);

public sealed record CreateModelRequest(
    string ModelKey,
    string? Name,
    string? Type,
    string? Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool? SupportsVision,
    bool? SupportsFunctionCall,
    bool? SupportsThinking,
    bool? Enabled);

public sealed record UpdateModelRequest(
    string? Name,
    string? Type,
    string? Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool? SupportsVision,
    bool? SupportsFunctionCall,
    bool? SupportsThinking,
    bool? Enabled);

/// <summary>Result of a connectivity test against a provider's upstream endpoint.</summary>
public sealed record ProviderCheckResponse(bool Ok, int? StatusCode, string? Error, int? ModelCount);

/// <summary>A model discovered by querying the provider's own catalog endpoint.</summary>
public sealed record DiscoveredModel(string ModelKey, string Name, string Category, string? Icon);

public sealed record DiscoveredModelsResponse(DiscoveredModel[] Models);

/// <summary>Adds a set of discovered models, skipping any the provider already has.</summary>
public sealed record ImportModelsRequest(CreateModelRequest[] Models);

public sealed record ImportModelsResponse(int Added);

// --- URL quick-config import ---

public sealed record ImportConfigRequest(ImportProviderEntry[] Providers);

public sealed record ImportProviderEntry(
    string? BuiltinId,
    string? Name,
    string? Description,
    string? Icon,
    int? PresetVersion,
    string? Type,
    string? ApiKey,
    string? BaseUrl,
    CreateModelRequest[]? Models);

public sealed record ImportConfigResponse(int Created, int Updated);
