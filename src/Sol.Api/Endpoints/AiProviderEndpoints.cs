using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Api.Serialization;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Api.Endpoints;

/// <summary>
/// Provider and model configuration, scoped to the calling device.
/// </summary>
/// <remarks>
/// Every route requires a device cookie and 401s without one. The handshake endpoint is the only
/// place identity is minted, so this never creates a device as a side effect of a config read.
/// <para>
/// Ownership is enforced in the repository's SQL rather than by a check here: a provider
/// belonging to another device is simply not found, so a caller cannot probe for valid ids.
/// </para>
/// </remarks>
public static class AiProviderEndpoints
{
    public static IEndpointRouteBuilder MapAiProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ai").WithTags("ai");

        group.MapGet("/providers", ListAsync).WithName("ListProviders");
        group.MapPost("/providers", CreateAsync)
            .WithName("CreateProvider")
            .RequireRateLimiting(RateLimitPolicies.ConfigWrite);
        group.MapPatch("/providers/{id}", UpdateAsync)
            .WithName("UpdateProvider")
            .RequireRateLimiting(RateLimitPolicies.ConfigWrite);
        group.MapDelete("/providers/{id}", DeleteAsync).WithName("DeleteProvider");
        group.MapPost("/providers/{id}/check", CheckAsync)
            .WithName("CheckProvider")
            .RequireRateLimiting(RateLimitPolicies.ConfigWrite);
        group.MapGet("/providers/{id}/upstream-models", ListUpstreamModelsAsync)
            .WithName("ListUpstreamModels");

        group.MapPost("/providers/{id}/models", AddModelAsync).WithName("AddModel");
        group.MapPost("/providers/{id}/models/import", ImportModelsAsync).WithName("ImportModels");
        group.MapPatch("/models/{modelId}", UpdateModelAsync).WithName("UpdateModel");
        group.MapDelete("/models/{modelId}", DeleteModelAsync).WithName("DeleteModel");

        return app;
    }

    private static async Task<Results<Ok<ProviderListResponse>, UnauthorizedHttpResult>> ListAsync(
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        var list = await providers.ListAsync(deviceId, ct);

        return TypedResults.Ok(new ProviderListResponse(
            [.. list.Select(AiContractMapper.ToResponse)]));
    }

    private static async Task<Results<Created<ProviderResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult, Conflict<ErrorResponse>>> CreateAsync(
        CreateProviderRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("name is required");
        }

        if (!AiEnumNames.TryParseProviderType(request.Type, out var type))
        {
            return Invalid($"unknown provider type '{request.Type}'");
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return Invalid("baseUrl is required");
        }

        if (await providers.FindByNameAsync(deviceId, request.Name, ct) is not null)
        {
            return TypedResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"A provider named '{request.Name}' already exists."]));
        }

        var now = DateTimeOffset.UtcNow;
        var providerId = ProviderId.New();

        var provider = new AiProvider(
            providerId,
            deviceId,
            request.BuiltinId,
            request.Name.Trim(),
            request.Description,
            request.Icon,
            type,
            string.IsNullOrWhiteSpace(request.ApiKey) ? null : protector.Protect(request.ApiKey),
            AiProvider.NormalizeBaseUrl(request.BaseUrl, type),
            request.Enabled ?? true,
            request.PresetVersion,
            await providers.NextSortOrderAsync(deviceId, ct),
            now,
            now);

        await providers.InsertAsync(provider, ct);

        if (request.Models is { Length: > 0 } models)
        {
            var built = new List<AiModel>(models.Length);
            for (var i = 0; i < models.Length; i++)
            {
                if (!TryBuildModel(models[i], providerId, i, now, out var model, out var error))
                {
                    return Invalid(error);
                }

                built.Add(model);
            }

            await providers.InsertModelsIfAbsentAsync(built, ct);
        }

        var created = await providers.FindAsync(deviceId, providerId, ct);

        return TypedResults.Created(
            $"/api/v1/ai/providers/{providerId}",
            AiContractMapper.ToResponse(created!));
    }

    private static async Task<Results<Ok<ProviderResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> UpdateAsync(
        string id,
        UpdateProviderRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var existing = await providers.FindAsync(deviceId, providerId, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        var type = existing.Type;
        if (request.Type is not null && !AiEnumNames.TryParseProviderType(request.Type, out type))
        {
            return Invalid($"unknown provider type '{request.Type}'");
        }

        // Three-state: null keeps the stored key (the common case when editing a base URL),
        // empty clears it, anything else replaces it. A plain nullable cannot say "keep".
        var keyUpdate = request.ApiKey switch
        {
            null => ApiKeyUpdate.Unchanged,
            "" => ApiKeyUpdate.Cleared,
            var key => ApiKeyUpdate.Replace(protector.Protect(key)),
        };

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            Description = request.Description ?? existing.Description,
            Icon = request.Icon ?? existing.Icon,
            Type = type,
            // Re-normalized on every update: the protocol may have changed in this same
            // request, which changes which suffix has to come off.
            BaseUrl = AiProvider.NormalizeBaseUrl(
                string.IsNullOrWhiteSpace(request.BaseUrl) ? existing.BaseUrl : request.BaseUrl,
                type),
            Enabled = request.Enabled ?? existing.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await providers.UpdateAsync(updated, keyUpdate, ct);

        var result = await providers.FindAsync(deviceId, providerId, ct);
        return TypedResults.Ok(AiContractMapper.ToResponse(result!));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> DeleteAsync(
        string id,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        return await providers.DeleteAsync(deviceId, providerId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    /// <summary>
    /// Tests whether the stored credentials reach the provider, by listing its models.
    /// </summary>
    private static async Task<Results<Ok<ProviderCheckResponse>, NotFound,
        BadRequest<ErrorResponse>, UnauthorizedHttpResult>> CheckAsync(
        string id,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IUpstreamModelCatalog catalog,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        if (!TryUnprotect(provider, protector, out var apiKey, out var keyError))
        {
            return TypedResults.Ok(new ProviderCheckResponse(false, null, keyError, null));
        }

        var result = await catalog.ListModelsAsync(provider, apiKey, ct);

        return TypedResults.Ok(new ProviderCheckResponse(
            result.Ok, result.StatusCode, result.Error, result.Ok ? result.Models.Count : null));
    }

    private static async Task<Results<Ok<DiscoveredModelsResponse>, NotFound,
        BadRequest<ErrorResponse>, UnauthorizedHttpResult>> ListUpstreamModelsAsync(
        string id,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IUpstreamModelCatalog catalog,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        if (!TryUnprotect(provider, protector, out var apiKey, out var keyError))
        {
            return Invalid(keyError);
        }

        var result = await catalog.ListModelsAsync(provider, apiKey, ct);

        if (!result.Ok)
        {
            return Invalid(result.Error ?? "upstream request failed");
        }

        return TypedResults.Ok(new DiscoveredModelsResponse(
        [
            .. result.Models.Select(model => new DiscoveredModel(
                model.ModelKey, model.Name, model.Category.ToWire(), null)),
        ]));
    }

    private static async Task<Results<Created<ModelResponse>, NotFound,
        BadRequest<ErrorResponse>, UnauthorizedHttpResult>> AddModelAsync(
        string id,
        CreateModelRequest request,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var sortOrder = provider.Models.Count == 0 ? 0 : provider.Models.Max(m => m.SortOrder) + 1;

        if (!TryBuildModel(request, providerId, sortOrder, DateTimeOffset.UtcNow,
                out var model, out var error))
        {
            return Invalid(error);
        }

        await providers.InsertModelAsync(model, ct);

        return TypedResults.Created(
            $"/api/v1/ai/models/{model.Id}", AiContractMapper.ToResponse(model));
    }

    /// <summary>Adds discovered models, skipping any already present. Safe to re-run.</summary>
    private static async Task<Results<Ok<ImportModelsResponse>, NotFound,
        BadRequest<ErrorResponse>, UnauthorizedHttpResult>> ImportModelsAsync(
        string id,
        ImportModelsRequest request,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var nextSort = provider.Models.Count == 0 ? 0 : provider.Models.Max(m => m.SortOrder) + 1;

        var models = new List<AiModel>(request.Models.Length);
        foreach (var entry in request.Models)
        {
            if (!TryBuildModel(entry, providerId, nextSort++, now, out var model, out var error))
            {
                return Invalid(error);
            }

            models.Add(model);
        }

        var added = await providers.InsertModelsIfAbsentAsync(models, ct);

        return TypedResults.Ok(new ImportModelsResponse(added));
    }

    private static async Task<Results<Ok<ModelResponse>, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> UpdateModelAsync(
        string modelId,
        UpdateModelRequest request,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ModelId.TryParse(modelId, out var parsedId))
        {
            return Invalid("malformed model id");
        }

        var existing = await providers.FindModelAsync(deviceId, parsedId, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        var type = existing.Type;
        if (request.Type is not null)
        {
            // An empty string clears the override so the model follows its provider again.
            if (request.Type.Length == 0)
            {
                type = null;
            }
            else if (AiEnumNames.TryParseProviderType(request.Type, out var parsedType))
            {
                type = parsedType;
            }
            else
            {
                return Invalid($"unknown provider type '{request.Type}'");
            }
        }

        var category = existing.Category;
        if (request.Category is not null
            && !AiEnumNames.TryParseModelCategory(request.Category, out category))
        {
            return Invalid($"unknown model category '{request.Category}'");
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            Type = type,
            Category = category,
            Icon = request.Icon ?? existing.Icon,
            ContextLength = request.ContextLength ?? existing.ContextLength,
            MaxOutputTokens = request.MaxOutputTokens ?? existing.MaxOutputTokens,
            SupportsVision = request.SupportsVision ?? existing.SupportsVision,
            SupportsFunctionCall = request.SupportsFunctionCall ?? existing.SupportsFunctionCall,
            SupportsThinking = request.SupportsThinking ?? existing.SupportsThinking,
            Enabled = request.Enabled ?? existing.Enabled,
        };

        await providers.UpdateModelAsync(updated, ct);

        return TypedResults.Ok(AiContractMapper.ToResponse(updated));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> DeleteModelAsync(
        string modelId,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ModelId.TryParse(modelId, out var parsedId))
        {
            return Invalid("malformed model id");
        }

        return await providers.DeleteModelAsync(deviceId, parsedId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static bool TryBuildModel(
        CreateModelRequest request,
        ProviderId providerId,
        int sortOrder,
        DateTimeOffset now,
        out AiModel model,
        out string error)
    {
        model = null!;

        if (string.IsNullOrWhiteSpace(request.ModelKey))
        {
            error = "modelKey is required";
            return false;
        }

        ProviderType? type = null;
        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!AiEnumNames.TryParseProviderType(request.Type, out var parsedType))
            {
                error = $"unknown provider type '{request.Type}'";
                return false;
            }

            type = parsedType;
        }

        var category = ModelCategory.Chat;
        if (request.Category is not null
            && !AiEnumNames.TryParseModelCategory(request.Category, out category))
        {
            error = $"unknown model category '{request.Category}'";
            return false;
        }

        var key = request.ModelKey.Trim();

        model = new AiModel(
            ModelId.New(),
            providerId,
            key,
            string.IsNullOrWhiteSpace(request.Name) ? key : request.Name.Trim(),
            request.Enabled ?? true,
            type,
            category,
            request.Icon,
            request.ContextLength,
            request.MaxOutputTokens,
            request.SupportsVision ?? false,
            request.SupportsFunctionCall ?? false,
            request.SupportsThinking ?? false,
            sortOrder,
            now);

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Recovers a provider's API key for an upstream call.
    /// </summary>
    /// <remarks>
    /// Decryption fails when the configured encryption key has changed or the row was tampered
    /// with. Neither is retryable, so the message tells the user to re-enter the key rather than
    /// presenting it as a transient error.
    /// </remarks>
    private static bool TryUnprotect(
        AiProvider provider,
        IApiKeyProtector protector,
        out string apiKey,
        out string error)
    {
        if (provider.ApiKey is null)
        {
            apiKey = string.Empty;
            error = string.Empty;
            return true;
        }

        try
        {
            apiKey = protector.Unprotect(provider.ApiKey);
            error = string.Empty;
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            apiKey = string.Empty;
            error = "The stored API key could not be decrypted. Re-enter it in settings.";
            return false;
        }
    }

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
