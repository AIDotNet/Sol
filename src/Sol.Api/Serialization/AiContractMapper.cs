using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;

namespace Sol.Api.Serialization;

/// <summary>
/// Maps domain entities to wire contracts.
/// </summary>
/// <remarks>
/// This is the only place a provider becomes JSON, which is what makes "the API key is never
/// serialized" a property of the system rather than a habit. <see cref="ProviderResponse"/> has
/// no field to put it in, and this mapper only ever reads <see cref="AiProvider.HasApiKey"/> and
/// the display hint.
/// </remarks>
internal static class AiContractMapper
{
    public static ProviderResponse ToResponse(AiProvider provider) => new(
        provider.Id.ToString(),
        provider.BuiltinId,
        provider.Name,
        provider.Description,
        provider.Icon,
        provider.Type.ToWire(),
        provider.BaseUrl,
        provider.Enabled,
        provider.HasApiKey,
        provider.ApiKey?.Hint,
        provider.SortOrder,
        provider.PresetVersion,
        provider.CreatedAt.ToString("O"),
        provider.UpdatedAt.ToString("O"),
        [.. provider.Models.Select(ToResponse)]);

    public static ModelResponse ToResponse(AiModel model) => new(
        model.Id.ToString(),
        model.ModelKey,
        model.Name,
        model.Enabled,
        model.Type?.ToWire(),
        model.Category.ToWire(),
        model.Icon,
        model.ContextLength,
        model.MaxOutputTokens,
        model.SupportsVision,
        model.SupportsFunctionCall,
        model.SupportsThinking,
        model.SortOrder);
}
