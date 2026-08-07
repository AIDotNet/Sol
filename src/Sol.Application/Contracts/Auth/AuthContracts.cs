namespace Sol.Application.Contracts.Auth;

public sealed record AuthProviderResponse(string Key, string DisplayName);

public sealed record AuthProvidersResponse(AuthProviderResponse[] Providers);

public sealed record AuthAccountResponse(
    string Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string[] ExternalProviders);

public sealed record AuthMeResponse(
    bool IsGuest,
    AuthAccountResponse? Account);
