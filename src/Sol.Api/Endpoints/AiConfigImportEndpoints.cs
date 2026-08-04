using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;

namespace Sol.Api.Endpoints;

/// <summary>
/// Applies a provider configuration bundle, as carried by a <c>?settings=</c> share link.
/// </summary>
/// <remarks>
/// The payload originates in a URL, so it is fully attacker-controlled. Two things keep that
/// contained: the client must show the user what will be written and get confirmation before
/// calling this, and everything here is scoped to the caller's own device — a crafted link can
/// only ever write to the device that opened it.
/// <para>
/// Upsert is by provider name, which the schema makes unique per device. Re-opening the same
/// link therefore updates in place rather than accumulating duplicates.
/// </para>
/// </remarks>
public static class AiConfigImportEndpoints
{
    /// <summary>
    /// Caps how many models one link may define per provider. A share link is meant to carry a
    /// working setup, not a bulk catalog load, and the request body is unauthenticated input.
    /// </summary>
    private const int MaxModelsPerProvider = 200;

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

        if (request.Providers.Length == 0)
        {
            return Invalid("at least one provider is required");
        }

        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var updated = 0;

        foreach (var entry in request.Providers)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                return Invalid("provider name is required");
            }

            if (!AiEnumNames.TryParseProviderType(entry.Type, out var type))
            {
                return Invalid($"unknown provider type '{entry.Type}'");
            }

            var models = entry.Models ?? [];
            if (models.Length > MaxModelsPerProvider)
            {
                return Invalid(
                    $"provider '{entry.Name}' declares {models.Length} models; "
                    + $"the limit is {MaxModelsPerProvider}");
            }

            var name = entry.Name.Trim();
            var existing = await providers.FindByNameAsync(deviceId, name, ct);

            ProviderId providerId;

            if (existing is null)
            {
                providerId = ProviderId.New();

                if (string.IsNullOrWhiteSpace(entry.BaseUrl))
                {
                    return Invalid($"provider '{name}' has no baseUrl and does not exist yet");
                }

                await providers.InsertAsync(
                    new AiProvider(
                        providerId,
                        deviceId,
                        entry.BuiltinId,
                        name,
                        null,
                        null,
                        type,
                        string.IsNullOrWhiteSpace(entry.ApiKey)
                            ? null
                            : protector.Protect(entry.ApiKey),
                        AiProvider.NormalizeBaseUrl(entry.BaseUrl, type),
                        Enabled: true,
                        PresetVersion: null,
                        await providers.NextSortOrderAsync(deviceId, ct),
                        now,
                        now),
                    ct);

                created++;
            }
            else
            {
                providerId = existing.Id;

                // A link that omits the key updates the URL and models but leaves the stored
                // credential alone, so sharing a setup link cannot wipe someone's key.
                var keyUpdate = string.IsNullOrWhiteSpace(entry.ApiKey)
                    ? ApiKeyUpdate.Unchanged
                    : ApiKeyUpdate.Replace(protector.Protect(entry.ApiKey));

                await providers.UpdateAsync(
                    existing with
                    {
                        Type = type,
                        BaseUrl = AiProvider.NormalizeBaseUrl(
                            string.IsNullOrWhiteSpace(entry.BaseUrl)
                                ? existing.BaseUrl
                                : entry.BaseUrl,
                            type),
                        UpdatedAt = now,
                    },
                    keyUpdate,
                    ct);

                updated++;
            }

            if (models.Length == 0)
            {
                continue;
            }

            var built = new List<AiModel>(models.Length);
            for (var i = 0; i < models.Length; i++)
            {
                var model = models[i];

                if (string.IsNullOrWhiteSpace(model.ModelKey))
                {
                    return Invalid($"provider '{name}' has a model with no modelKey");
                }

                ProviderType? modelType = null;
                if (!string.IsNullOrEmpty(model.Type))
                {
                    if (!AiEnumNames.TryParseProviderType(model.Type, out var parsed))
                    {
                        return Invalid($"unknown provider type '{model.Type}'");
                    }

                    modelType = parsed;
                }

                var category = ModelCategory.Chat;
                if (model.Category is not null
                    && !AiEnumNames.TryParseModelCategory(model.Category, out category))
                {
                    return Invalid($"unknown model category '{model.Category}'");
                }

                var key = model.ModelKey.Trim();

                built.Add(new AiModel(
                    ModelId.New(),
                    providerId,
                    key,
                    string.IsNullOrWhiteSpace(model.Name) ? key : model.Name.Trim(),
                    model.Enabled ?? true,
                    modelType,
                    category,
                    model.Icon,
                    model.ContextLength,
                    model.MaxOutputTokens,
                    model.SupportsVision ?? false,
                    model.SupportsFunctionCall ?? false,
                    model.SupportsThinking ?? false,
                    i,
                    now));
            }

            // Absent-only, so re-opening a link cannot overwrite a user's own per-model edits.
            await providers.InsertModelsIfAbsentAsync(built, ct);
        }

        return TypedResults.Ok(new ImportConfigResponse(created, updated));
    }

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
