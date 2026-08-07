using Sol.Application.Abstractions.Security;

namespace Sol.Infrastructure.Identity;

internal sealed class ExternalLoginProviderRegistry(
    IEnumerable<IExternalLoginProvider> configuredProviders)
    : IExternalLoginProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IExternalLoginProvider> providers =
        configuredProviders
            .Where(provider => provider.IsEnabled)
            .ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);

    public IExternalLoginProvider? Find(string key) =>
        providers.TryGetValue(key, out var provider) ? provider : null;

    public IReadOnlyList<ExternalLoginProviderDescriptor> ListEnabled() =>
        providers.Values
            .OrderBy(provider => provider.Key, StringComparer.Ordinal)
            .Select(provider => new ExternalLoginProviderDescriptor(
                provider.Key,
                provider.DisplayName))
            .ToArray();
}
