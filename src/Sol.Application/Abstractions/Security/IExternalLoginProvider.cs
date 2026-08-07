using Sol.Application.Abstractions.Persistence;

namespace Sol.Application.Abstractions.Security;

/// <summary>
/// Provider-neutral OAuth adapter. Adding another provider is a new adapter plus one explicit DI
/// registration; account/session tables do not contain provider-specific columns.
/// </summary>
public interface IExternalLoginProvider
{
    string Key { get; }
    string DisplayName { get; }
    bool IsEnabled { get; }

    string BuildAuthorizationUrl(
        Uri callbackUri,
        string state,
        string codeChallenge);

    Task<ExternalLoginProfile> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        Uri callbackUri,
        CancellationToken ct);
}

public interface IExternalLoginProviderRegistry
{
    IExternalLoginProvider? Find(string key);
    IReadOnlyList<ExternalLoginProviderDescriptor> ListEnabled();
}

public sealed record ExternalLoginProviderDescriptor(string Key, string DisplayName);
