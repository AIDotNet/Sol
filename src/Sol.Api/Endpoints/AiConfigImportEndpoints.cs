using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;

namespace Sol.Api.Endpoints;

/// <summary>
/// Applies a provider configuration bundle carried by a <c>?settings=</c> or
/// <c>#settings=</c> share link.
/// </summary>
/// <remarks>
/// The browser resolves built-in presets before calling this endpoint. The endpoint remains the
/// authority for ownership, normalization, URL validation, API-key protection and model input
/// validation. It never fetches a provider's upstream catalog as part of an import.
/// </remarks>
public static class AiConfigImportEndpoints
{
    private const int MaxProviders = 50;
    private const int MaxModelsPerProvider = 200;
    private const int MaxProviderNameLength = 200;
    private const int MaxBuiltinIdLength = 64;
    private const int MaxBaseUrlLength = 2048;
    private const int MaxApiKeyLength = 4096;
    private const int MaxDescriptionLength = 1000;
    private const int MaxModelKeyLength = 200;
    private const int MaxModelNameLength = 200;
    private const int MaxIconLength = 2048;
    private const int MaxPresetVersion = 100_000;

    public static IEndpointRouteBuilder MapAiConfigImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/config/import", ImportAsync)
            .WithTags("ai")
            .WithName("ImportAiConfig");

        return app;
    }

    private static async Task<Results<Ok<ImportConfigResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> ImportAsync(
        ImportConfigRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (request.Providers is null || request.Providers.Length == 0)
        {
            return Invalid("at least one provider is required");
        }

        if (request.Providers.Length > MaxProviders)
        {
            return Invalid($"at most {MaxProviders} providers may be imported");
        }

        // Validate and resolve every entry before the first write. Besides producing better error
        // messages, this prevents a malformed later entry from leaving an earlier provider half
        // imported.
        var plans = new List<ImportPlan>(request.Providers.Length);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in request.Providers)
        {
            if (entry is null)
            {
                return Invalid("provider entry is required");
            }

            var builtinId = NormalizeOptional(entry.BuiltinId);
            var requestedName = NormalizeOptional(entry.Name);

            AiProvider? existing = null;
            if (builtinId is not null)
            {
                existing = await providers.FindByBuiltinIdAsync(deviceId, builtinId, ct);
            }

            // This fallback lets a new version of the URL importer adopt a provider created by
            // an older version, before stable builtin-id lookup existed.
            if (existing is null && requestedName is not null)
            {
                existing = await providers.FindByNameAsync(deviceId, requestedName, ct);
            }

            var name = requestedName ?? existing?.Name ?? builtinId;
            if (name is null)
            {
                return Invalid("provider name is required");
            }

            if (name.Length > MaxProviderNameLength)
            {
                return Invalid(
                    $"provider name must be at most {MaxProviderNameLength} characters");
            }

            if (builtinId is { Length: > MaxBuiltinIdLength })
            {
                return Invalid(
                    $"provider '{name}' has a built-in id longer than {MaxBuiltinIdLength} characters");
            }

            var identity = builtinId is not null
                ? $"builtin:{builtinId}"
                : $"name:{name}";
            if (!identities.Add(identity))
            {
                return Invalid($"provider '{name}' appears more than once in the request");
            }

            if (!names.Add(name))
            {
                return Invalid($"provider name '{name}' appears more than once in the request");
            }

            ProviderType type;
            if (entry.Type is not null)
            {
                if (!AiEnumNames.TryParseProviderType(entry.Type, out type))
                {
                    return Invalid($"unknown provider type '{entry.Type}'");
                }
            }
            else if (existing is not null)
            {
                type = existing.Type;
            }
            else
            {
                return Invalid($"provider '{name}' has no type");
            }

            string baseUrl;
            if (entry.BaseUrl is not null)
            {
                if (!TryNormalizeBaseUrl(entry.BaseUrl, type, out baseUrl, out var urlError))
                {
                    return Invalid($"provider '{name}' {urlError}");
                }
            }
            else if (existing is not null)
            {
                // Re-normalize an existing URL if the protocol was explicitly changed in the
                // same request. The browser normally sends the fully resolved URL, but keeping
                // this fallback makes direct API clients safe too.
                if (!TryNormalizeBaseUrl(existing.BaseUrl, type, out baseUrl, out var urlError))
                {
                    return Invalid($"provider '{name}' {urlError}");
                }
            }
            else
            {
                return Invalid($"provider '{name}' has no baseUrl and does not exist yet");
            }

            if (entry.ApiKey is not null && string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                return Invalid($"provider '{name}' apiKey cannot be empty; omit it to keep the stored key");
            }

            if (entry.ApiKey is { Length: > MaxApiKeyLength })
            {
                return Invalid(
                    $"provider '{name}' apiKey is longer than {MaxApiKeyLength} characters");
            }

            if (baseUrl.Length > MaxBaseUrlLength)
            {
                return Invalid(
                    $"provider '{name}' baseUrl is longer than {MaxBaseUrlLength} characters");
            }

            if (entry.Description is { Length: > MaxDescriptionLength })
            {
                return Invalid(
                    $"provider '{name}' description is longer than {MaxDescriptionLength} characters");
            }

            if (entry.Icon is { Length: > MaxIconLength })
            {
                return Invalid($"provider '{name}' icon is too long");
            }

            if (entry.PresetVersion is <= 0 or > MaxPresetVersion)
            {
                return Invalid($"provider '{name}' has an invalid presetVersion");
            }

            var definitions = new List<ModelDefinition>();
            var modelKeys = new HashSet<string>(StringComparer.Ordinal);
            var models = entry.Models ?? [];
            if (models.Length > MaxModelsPerProvider)
            {
                return Invalid(
                    $"provider '{name}' declares {models.Length} models; "
                    + $"the limit is {MaxModelsPerProvider}");
            }

            foreach (var model in models)
            {
                if (model is null)
                {
                    return Invalid($"provider '{name}' contains a null model");
                }

                if (!TryValidateModel(model, out var definition, out var modelError))
                {
                    return Invalid($"provider '{name}' {modelError}");
                }

                if (!modelKeys.Add(definition.ModelKey))
                {
                    return Invalid(
                        $"provider '{name}' declares model '{definition.ModelKey}' more than once");
                }

                definitions.Add(definition);
            }

            plans.Add(new ImportPlan(
                entry,
                existing,
                builtinId,
                name,
                type,
                baseUrl,
                definitions));
        }

        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var updated = 0;

        foreach (var plan in plans)
        {
            var providerId = plan.Existing?.Id ?? ProviderId.New();
            var secret = plan.Entry.ApiKey is null
                ? null
                : protector.Protect(plan.Entry.ApiKey);

            if (plan.Existing is null)
            {
                await providers.InsertAsync(
                    new AiProvider(
                        providerId,
                        deviceId,
                        plan.BuiltinId,
                        plan.Name,
                        plan.Entry.Description,
                        plan.Entry.Icon,
                        plan.Type,
                        secret,
                        plan.BaseUrl,
                        true,
                        plan.Entry.PresetVersion,
                        await providers.NextSortOrderAsync(deviceId, ct),
                        now,
                        now),
                    ct);

                created++;
            }
            else
            {
                var existing = plan.Existing;
                var keyUpdate = secret is null
                    ? ApiKeyUpdate.Unchanged
                    : ApiKeyUpdate.Replace(secret);

                await providers.UpdateAsync(
                    existing with
                    {
                        // An explicit builtin id can adopt a legacy row found by name. A custom
                        // import never removes an existing builtin marker.
                        BuiltinId = plan.BuiltinId ?? existing.BuiltinId,
                        Name = plan.Name,
                        Description = plan.Entry.Description ?? existing.Description,
                        Icon = plan.Entry.Icon ?? existing.Icon,
                        Type = plan.Type,
                        BaseUrl = plan.BaseUrl,
                        PresetVersion = plan.Entry.PresetVersion ?? existing.PresetVersion,
                        UpdatedAt = now,
                    },
                    keyUpdate,
                    ct);

                updated++;
            }

            if (plan.Models.Count == 0)
            {
                continue;
            }

            var built = plan.Models
                .Select((model, index) => new AiModel(
                    ModelId.New(),
                    providerId,
                    model.ModelKey,
                    model.Name,
                    model.Enabled,
                    model.Type,
                    model.Category,
                    model.Icon,
                    model.ContextLength,
                    model.MaxOutputTokens,
                    model.SupportsVision,
                    model.SupportsFunctionCall,
                    model.SupportsThinking,
                    index,
                    now))
                .ToList();

            // Absent-only, so re-opening a link cannot overwrite a user's per-model edits.
            await providers.InsertModelsIfAbsentAsync(built, ct);
        }

        return TypedResults.Ok(new ImportConfigResponse(created, updated));
    }

    private static bool TryNormalizeBaseUrl(
        string value,
        ProviderType type,
        out string normalized,
        out string error)
    {
        if (AiProvider.TryNormalizeBaseUrl(value, type, out normalized, out var detail))
        {
            error = string.Empty;
            return true;
        }

        error = detail;
        return false;
    }

    private static bool TryValidateModel(
        CreateModelRequest model,
        out ModelDefinition definition,
        out string error)
    {
        definition = default!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(model.ModelKey))
        {
            error = "has a model with no modelKey";
            return false;
        }

        var key = model.ModelKey.Trim();
        if (key.Length > MaxModelKeyLength)
        {
            error = $"has a model key longer than {MaxModelKeyLength} characters";
            return false;
        }

        ProviderType? modelType = null;
        if (model.Type is not null)
        {
            if (string.IsNullOrWhiteSpace(model.Type)
                || !AiEnumNames.TryParseProviderType(model.Type, out var parsed))
            {
                error = $"has model '{key}' with unknown provider type '{model.Type}'";
                return false;
            }

            modelType = parsed;
        }

        var category = ModelCategory.Chat;
        if (model.Category is not null
            && !AiEnumNames.TryParseModelCategory(model.Category, out category))
        {
            error = $"has model '{key}' with unknown category '{model.Category}'";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(model.Name) ? key : model.Name.Trim();
        if (name.Length > MaxModelNameLength)
        {
            error = $"has model '{key}' with a name longer than {MaxModelNameLength} characters";
            return false;
        }

        if (model.ContextLength is <= 0 or > 100_000_000)
        {
            error = $"has model '{key}' with an invalid contextLength";
            return false;
        }

        if (model.MaxOutputTokens is <= 0 or > 10_000_000)
        {
            error = $"has model '{key}' with an invalid maxOutputTokens";
            return false;
        }

        if (model.Icon is { Length: > MaxIconLength })
        {
            error = $"has model '{key}' with an icon that is too long";
            return false;
        }

        definition = new ModelDefinition(
            key,
            name,
            modelType,
            category,
            model.Icon,
            model.ContextLength,
            model.MaxOutputTokens,
            model.SupportsVision ?? false,
            model.SupportsFunctionCall ?? false,
            model.SupportsThinking ?? false,
            model.Enabled ?? true);
        return true;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));

    private sealed record ImportPlan(
        ImportProviderEntry Entry,
        AiProvider? Existing,
        string? BuiltinId,
        string Name,
        ProviderType Type,
        string BaseUrl,
        IReadOnlyList<ModelDefinition> Models);

    private sealed record ModelDefinition(
        string ModelKey,
        string Name,
        ProviderType? Type,
        ModelCategory Category,
        string? Icon,
        int? ContextLength,
        int? MaxOutputTokens,
        bool SupportsVision,
        bool SupportsFunctionCall,
        bool SupportsThinking,
        bool Enabled);
}
