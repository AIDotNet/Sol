using Sol.Domain.Identity;

namespace Sol.Application.Abstractions.Persistence;

/// <summary>
/// Durable account, external-login, OAuth transaction, and session storage.
/// </summary>
/// <remarks>
/// Raw OAuth state and session tokens never cross this port. Callers pass only hashes for values
/// that can authenticate a browser, so a database read or log cannot be replayed as a session.
/// </remarks>
public interface IAccountRepository
{
    Task<AccountProfile?> FindBySessionTokenHashAsync(
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken ct);

    Task<AccountProfile?> FindByIdAsync(AccountId accountId, CancellationToken ct);

    Task<AccountId?> FindByExternalIdentityAsync(
        string providerKey,
        string subject,
        CancellationToken ct);

    Task<AccountProfile> GetOrCreateFromExternalAsync(
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct);

    Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct);

    Task CreateSessionAsync(AccountSession session, CancellationToken ct);

    Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct);

    Task<AccountId?> FindAccountForDeviceAsync(DeviceId deviceId, CancellationToken ct);

    Task<bool> LinkDeviceAsync(AccountId accountId, DeviceId deviceId, CancellationToken ct);

    Task CreateOAuthTransactionAsync(OAuthTransaction transaction, CancellationToken ct);

    Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
        byte[] stateHash,
        DateTimeOffset now,
        CancellationToken ct);
}

public sealed record AccountProfile(
    AccountId Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    IReadOnlyList<string> ExternalProviders);

/// <summary>Normalized identity returned by a provider adapter.</summary>
public sealed record ExternalLoginProfile(
    string ProviderKey,
    string Subject,
    string DisplayName,
    string? Email,
    string? AvatarUrl);

public sealed record AccountSession(
    Guid SessionId,
    AccountId AccountId,
    byte[] TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? UserAgent,
    string? IpAddress);

public sealed record OAuthTransaction(
    Guid TransactionId,
    byte[] StateHash,
    string ProviderKey,
    AccountId? LinkAccountId,
    DeviceId? DeviceId,
    string ReturnPath,
    string CodeVerifier,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public enum ExternalIdentityLinkResult
{
    Linked,
    AlreadyLinked,
    BelongsToAnotherAccount,
    ProviderAlreadyLinked,
}
