using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Domain.Identity;

namespace Sol.Application.Features.Identity;

/// <summary>
/// Coordinates provider-neutral OAuth with durable account and session storage.
/// </summary>
public sealed class AccountAuthService(
    IAccountRepository accounts,
    IExternalLoginProviderRegistry providers,
    IOptions<AuthenticationOptions> configuredOptions)
{
    private readonly AuthenticationOptions options = configuredOptions.Value;

    public async Task<ExternalLoginStart> BeginAsync(
        string providerKey,
        Uri callbackUri,
        string returnPath,
        DeviceId? deviceId,
        AccountId? linkAccountId,
        CancellationToken ct)
    {
        var provider = providers.Find(providerKey)
            ?? throw new ExternalLoginException("provider_unavailable");

        var normalizedReturnPath = NormalizeReturnPath(returnPath);
        var now = DateTimeOffset.UtcNow;
        var state = CreateToken(32);
        var verifier = CreateToken(48);
        var transaction = new OAuthTransaction(
            Guid.CreateVersion7(),
            HashToken(state),
            provider.Key,
            linkAccountId,
            deviceId,
            normalizedReturnPath,
            verifier,
            now,
            now.AddMinutes(Math.Clamp(options.OAuthTransactionMinutes, 1, 30)));

        await accounts.CreateOAuthTransactionAsync(transaction, ct);

        return new ExternalLoginStart(
            provider.BuildAuthorizationUrl(callbackUri, state, CreateCodeChallenge(verifier)),
            normalizedReturnPath);
    }

    public async Task<ExternalLoginCompletion> CompleteAsync(
        string providerKey,
        string code,
        string state,
        Uri callbackUri,
        DeviceId? currentDeviceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new ExternalLoginException("invalid_callback");
        }

        var transaction = await accounts.ConsumeOAuthTransactionAsync(HashToken(state), DateTimeOffset.UtcNow, ct)
            ?? throw new ExternalLoginException("invalid_or_expired_state");

        if (!string.Equals(transaction.ProviderKey, providerKey, StringComparison.Ordinal))
        {
            throw new ExternalLoginException("provider_mismatch");
        }

        var provider = providers.Find(providerKey)
            ?? throw new ExternalLoginException("provider_unavailable");

        ExternalLoginProfile profile;
        try
        {
            profile = await provider.ExchangeCodeAsync(code, transaction.CodeVerifier, callbackUri, ct);
        }
        catch (ExternalLoginException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalLoginException("provider_unreachable", exception);
        }

        var deviceId = currentDeviceId ?? transaction.DeviceId;
        var deviceOwner = deviceId is { } current
            ? await accounts.FindAccountForDeviceAsync(current, ct)
            : null;

        AccountProfile account;
        if (transaction.LinkAccountId is { } linkAccountId)
        {
            var link = await accounts.LinkExternalIdentityAsync(
                linkAccountId, profile, DateTimeOffset.UtcNow, ct);
            if (link is ExternalIdentityLinkResult.BelongsToAnotherAccount
                or ExternalIdentityLinkResult.ProviderAlreadyLinked)
            {
                throw new ExternalLoginException("provider_already_linked");
            }

            account = await accounts.FindByIdAsync(linkAccountId, ct)
                ?? throw new ExternalLoginException("account_not_found");
        }
        else
        {
            var existingIdentityAccount = await accounts.FindByExternalIdentityAsync(
                profile.ProviderKey, profile.Subject, ct);

            // A device already owned by an account must not be silently switched to another
            // account merely because a different provider was used. The user can explicitly log
            // out (which starts a fresh guest device) or use the account's link flow.
            if (deviceOwner is { } owner &&
                (existingIdentityAccount is null || existingIdentityAccount.Value != owner))
            {
                throw new ExternalLoginException("device_belongs_to_another_account");
            }

            account = existingIdentityAccount is { } known
                ? await accounts.FindByIdAsync(known, ct)
                    ?? throw new ExternalLoginException("account_not_found")
                : await accounts.GetOrCreateFromExternalAsync(profile, DateTimeOffset.UtcNow, ct);
        }

        if (deviceId is { } device)
        {
            if (deviceOwner is { } owner && owner != account.Id)
            {
                throw new ExternalLoginException("device_belongs_to_another_account");
            }

            if (!await accounts.LinkDeviceAsync(account.Id, device, ct))
            {
                throw new ExternalLoginException("device_belongs_to_another_account");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var rawSessionToken = CreateToken(32);
        await accounts.CreateSessionAsync(
            new AccountSession(
                Guid.CreateVersion7(),
                account.Id,
                HashToken(rawSessionToken),
                now,
                now.AddDays(Math.Clamp(options.SessionMaxAgeDays, 1, 365)),
                null,
                null),
            ct);

        return new ExternalLoginCompletion(
            rawSessionToken,
            account,
            transaction.ReturnPath);
    }

    public static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string NormalizeReturnPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith('/', StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains("\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return value.Length <= 512 ? value : "/";
    }

    private static string CreateToken(int byteCount) =>
        Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record ExternalLoginStart(string AuthorizationUrl, string ReturnPath);

public sealed record ExternalLoginCompletion(
    string RawSessionToken,
    AccountProfile Account,
    string ReturnPath);

public sealed class ExternalLoginException(string code, Exception? inner = null)
    : Exception(code, inner)
{
    public string Code { get; } = code;
}
