using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

/// <summary>
/// Provider and model storage. Every method is scoped by <see cref="DeviceId"/> — there is no
/// way to read a provider without stating whose it is, which keeps the ownership check from
/// being something a caller can forget.
/// </summary>
public interface IProviderRepository
{
    /// <summary>Lists a device's providers with their models attached, in display order.</summary>
    Task<IReadOnlyList<AiProvider>> ListAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>
    /// Loads one provider with its models. Returns null when it does not exist <em>or</em>
    /// belongs to another device, so a caller cannot distinguish the two and probe for ids.
    /// </summary>
    Task<AiProvider?> FindAsync(DeviceId deviceId, ProviderId providerId, CancellationToken ct);

    /// <summary>Looks a provider up by its unique per-device name, for URL import upserts.</summary>
    Task<AiProvider?> FindByNameAsync(DeviceId deviceId, string name, CancellationToken ct);

    Task InsertAsync(AiProvider provider, CancellationToken ct);

    /// <summary>
    /// Updates the mutable provider fields.
    /// </summary>
    /// <remarks>
    /// <paramref name="apiKey"/> is a three-state value: a secret replaces the stored key,
    /// <see cref="ApiKeyUpdate.Unchanged"/> leaves it alone, and <see cref="ApiKeyUpdate.Cleared"/>
    /// removes it. A plain nullable cannot express "leave it alone", which is the common case
    /// when a user edits a base URL — and getting it wrong silently wipes the key.
    /// </remarks>
    Task UpdateAsync(AiProvider provider, ApiKeyUpdate apiKey, CancellationToken ct);

    Task<bool> DeleteAsync(DeviceId deviceId, ProviderId providerId, CancellationToken ct);

    Task<int> NextSortOrderAsync(DeviceId deviceId, CancellationToken ct);

    // --- models ---

    Task InsertModelAsync(AiModel model, CancellationToken ct);

    Task UpdateModelAsync(AiModel model, CancellationToken ct);

    /// <summary>Deletes a model, verifying it belongs to a provider owned by this device.</summary>
    Task<bool> DeleteModelAsync(DeviceId deviceId, ModelId modelId, CancellationToken ct);

    Task<AiModel?> FindModelAsync(DeviceId deviceId, ModelId modelId, CancellationToken ct);

    /// <summary>
    /// Adds models that do not already exist on the provider, keyed by <c>model_key</c>.
    /// Used by "fetch models" and URL import, where re-running must not duplicate rows or
    /// overwrite a user's per-model edits.
    /// </summary>
    Task<int> InsertModelsIfAbsentAsync(IReadOnlyList<AiModel> models, CancellationToken ct);

    Task SetModelsEnabledAsync(ProviderId providerId, bool enabled, CancellationToken ct);
}

/// <summary>How an update should treat the stored API key.</summary>
public readonly record struct ApiKeyUpdate
{
    private ApiKeyUpdate(ApiKeySecret? secret, bool clear)
    {
        Secret = secret;
        Clear = clear;
    }

    public ApiKeySecret? Secret { get; }

    public bool Clear { get; }

    /// <summary>Leave whatever is stored in place.</summary>
    public static ApiKeyUpdate Unchanged => new(null, false);

    /// <summary>Remove the stored key.</summary>
    public static ApiKeyUpdate Cleared => new(null, true);

    public static ApiKeyUpdate Replace(ApiKeySecret secret) => new(secret, false);
}
