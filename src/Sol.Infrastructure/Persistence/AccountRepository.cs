using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL-backed account repository. Provider identities are normalized into a single table,
/// so adding another OAuth provider never changes the account schema.
/// </summary>
public sealed class AccountRepository(NpgsqlDataSource dataSource) : IAccountRepository
{
    public async Task<AccountProfile?> FindBySessionTokenHashAsync(
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accountId = await connection.QueryFirstOrDefaultAsync<Guid?>
        (
            """
            SELECT account_id
            FROM sol_auth_session
            WHERE token_hash = @TokenHash AND expires_at > @Now
            """,
            new SessionLookupArgs { TokenHash = tokenHash, Now = now });

        if (accountId is not { } value) return null;

        await connection.ExecuteAsync(
            "UPDATE sol_auth_session SET last_seen_at = @Now WHERE token_hash = @TokenHash",
            new SessionLookupArgs { TokenHash = tokenHash, Now = now });

        return await LoadProfileAsync(connection, new AccountId(value));
    }

    public async Task<AccountProfile?> FindByIdAsync(
        AccountId accountId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await LoadProfileAsync(connection, accountId);
    }

    public async Task<AccountId?> FindByExternalIdentityAsync(
        string providerKey,
        string subject,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accountId = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM sol_external_identity
            WHERE provider_key = @ProviderKey AND subject = @Subject
            """,
            new ExternalIdentityLookupArgs { ProviderKey = providerKey, Subject = subject });

        return accountId is { } value ? new AccountId(value) : null;
    }

    public async Task<AccountProfile> GetOrCreateFromExternalAsync(
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var existing = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM sol_external_identity
            WHERE provider_key = @ProviderKey AND subject = @Subject
            """,
            new ExternalIdentityLookupArgs
            {
                ProviderKey = profile.ProviderKey,
                Subject = profile.Subject,
            },
            transaction);

        var accountId = existing is { } known
            ? new AccountId(known)
            : AccountId.New();

        if (existing is null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO sol_account (account_id, display_name, email, avatar_url, created_at, updated_at)
                VALUES (@AccountId, @DisplayName, @Email, @AvatarUrl, @Now, @Now)
                """,
                new AccountWriteArgs
                {
                    AccountId = accountId.Value,
                    DisplayName = profile.DisplayName,
                    Email = profile.Email,
                    AvatarUrl = profile.AvatarUrl,
                    Now = now,
                },
                transaction);

            var inserted = await connection.ExecuteAsync(
                """
                INSERT INTO sol_external_identity
                    (external_identity_id, account_id, provider_key, subject, display_name,
                     email, avatar_url, created_at, last_login_at)
                VALUES
                    (@ExternalIdentityId, @AccountId, @ProviderKey, @Subject, @DisplayName,
                     @Email, @AvatarUrl, @Now, @Now)
                ON CONFLICT (provider_key, subject) DO NOTHING
                """,
                new ExternalIdentityWriteArgs
                {
                    ExternalIdentityId = Guid.CreateVersion7(),
                    AccountId = accountId.Value,
                    ProviderKey = profile.ProviderKey,
                    Subject = profile.Subject,
                    DisplayName = profile.DisplayName,
                    Email = profile.Email,
                    AvatarUrl = profile.AvatarUrl,
                    Now = now,
                },
                transaction);

            if (inserted == 0)
            {
                // Another request won the provider-subject race. Remove the unreferenced account
                // created above and use the winner, all within the same transaction.
                var winner = await connection.QuerySingleAsync<Guid>(
                    """
                    SELECT account_id
                    FROM sol_external_identity
                    WHERE provider_key = @ProviderKey AND subject = @Subject
                    """,
                    new ExternalIdentityLookupArgs
                    {
                        ProviderKey = profile.ProviderKey,
                        Subject = profile.Subject,
                    },
                    transaction);
                await connection.ExecuteAsync(
                    "DELETE FROM sol_account WHERE account_id = @AccountId",
                    new AccountIdArgs { AccountId = accountId.Value },
                    transaction);
                accountId = new AccountId(winner);
            }
        }

        await UpdateExternalIdentityAsync(connection, transaction, accountId, profile, now);
        await transaction.CommitAsync(ct);

        return await LoadProfileAsync(connection, accountId)
            ?? throw new InvalidOperationException("The external identity account disappeared.");
    }

    public async Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var existing = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM sol_external_identity
            WHERE provider_key = @ProviderKey AND subject = @Subject
            """,
            new ExternalIdentityLookupArgs
            {
                ProviderKey = profile.ProviderKey,
                Subject = profile.Subject,
            });

        if (existing is { } existingAccount && existingAccount != accountId.Value)
        {
            return ExternalIdentityLinkResult.BelongsToAnotherAccount;
        }

        if (existing is { } sameAccount && sameAccount == accountId.Value)
        {
            return ExternalIdentityLinkResult.AlreadyLinked;
        }

        var providerAlreadyLinked = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM sol_external_identity
            WHERE account_id = @AccountId AND provider_key = @ProviderKey
            """,
            new AccountProviderArgs
            {
                AccountId = accountId.Value,
                ProviderKey = profile.ProviderKey,
            });

        if (providerAlreadyLinked is { } linkedAccount)
        {
            return linkedAccount == accountId.Value
                ? ExternalIdentityLinkResult.ProviderAlreadyLinked
                : ExternalIdentityLinkResult.BelongsToAnotherAccount;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO sol_external_identity
                (external_identity_id, account_id, provider_key, subject, display_name,
                 email, avatar_url, created_at, last_login_at)
            VALUES
                (@ExternalIdentityId, @AccountId, @ProviderKey, @Subject, @DisplayName,
                 @Email, @AvatarUrl, @Now, @Now)
            """,
            new ExternalIdentityWriteArgs
            {
                ExternalIdentityId = Guid.CreateVersion7(),
                AccountId = accountId.Value,
                ProviderKey = profile.ProviderKey,
                Subject = profile.Subject,
                DisplayName = profile.DisplayName,
                Email = profile.Email,
                AvatarUrl = profile.AvatarUrl,
                Now = now,
            });

        await UpdateAccountProfileAsync(connection, accountId, profile, now);
        return ExternalIdentityLinkResult.Linked;
    }

    public async Task CreateSessionAsync(AccountSession session, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO sol_auth_session
                (session_id, account_id, token_hash, created_at, expires_at, last_seen_at,
                 user_agent, ip_address)
            VALUES
                (@SessionId, @AccountId, @TokenHash, @CreatedAt, @ExpiresAt, @CreatedAt,
                 @UserAgent, NULLIF(@IpAddress, '')::inet)
            """,
            new SessionWriteArgs
            {
                SessionId = session.SessionId,
                AccountId = session.AccountId.Value,
                TokenHash = session.TokenHash,
                CreatedAt = session.CreatedAt,
                ExpiresAt = session.ExpiresAt,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
            });
    }

    public async Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM sol_auth_session WHERE token_hash = @TokenHash",
            new TokenHashArgs { TokenHash = tokenHash });
    }

    public async Task<AccountId?> FindAccountForDeviceAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accountId = await connection.QueryFirstOrDefaultAsync<Guid?>(
            "SELECT account_id FROM sol_account_device WHERE device_id = @DeviceId",
            new DeviceIdArgs { DeviceId = deviceId.Value });
        return accountId is { } value ? new AccountId(value) : null;
    }

    public async Task<bool> LinkDeviceAsync(
        AccountId accountId,
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var inserted = await connection.ExecuteAsync(
            """
            INSERT INTO sol_account_device (account_id, device_id, linked_at)
            VALUES (@AccountId, @DeviceId, @LinkedAt)
            ON CONFLICT (device_id) DO NOTHING
            """,
            new AccountDeviceArgs
            {
                AccountId = accountId.Value,
                DeviceId = deviceId.Value,
                LinkedAt = DateTimeOffset.UtcNow,
            });

        if (inserted > 0) return true;

        var owner = await connection.QueryFirstOrDefaultAsync<Guid?>(
            "SELECT account_id FROM sol_account_device WHERE device_id = @DeviceId",
            new DeviceIdArgs { DeviceId = deviceId.Value });
        return owner == accountId.Value;
    }

    public async Task CreateOAuthTransactionAsync(
        OAuthTransaction transaction,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO sol_oauth_transaction
                (transaction_id, state_hash, provider_key, link_account_id, device_id,
                 return_path, code_verifier, created_at, expires_at)
            VALUES
                (@TransactionId, @StateHash, @ProviderKey, @LinkAccountId, @DeviceId,
                 @ReturnPath, @CodeVerifier, @CreatedAt, @ExpiresAt)
            """,
            new OAuthTransactionArgs
            {
                TransactionId = transaction.TransactionId,
                StateHash = transaction.StateHash,
                ProviderKey = transaction.ProviderKey,
                LinkAccountId = transaction.LinkAccountId?.Value,
                DeviceId = transaction.DeviceId?.Value,
                ReturnPath = transaction.ReturnPath,
                CodeVerifier = transaction.CodeVerifier,
                CreatedAt = transaction.CreatedAt,
                ExpiresAt = transaction.ExpiresAt,
            });
    }

    public async Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
        byte[] stateHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<OAuthTransactionRow>(
            """
            DELETE FROM sol_oauth_transaction
            WHERE state_hash = @StateHash AND expires_at > @Now
            RETURNING transaction_id, state_hash, provider_key, link_account_id, device_id,
                      return_path, code_verifier, created_at, expires_at
            """,
            new OAuthConsumeArgs { StateHash = stateHash, Now = now });
        return row?.ToDomain();
    }

    private static async Task<AccountProfile?> LoadProfileAsync(
        NpgsqlConnection connection,
        AccountId accountId,
        NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QueryFirstOrDefaultAsync<AccountRow>(
            """
            SELECT account_id, display_name, email, avatar_url
            FROM sol_account
            WHERE account_id = @AccountId
            """,
            new AccountIdArgs { AccountId = accountId.Value },
            transaction);
        if (row is null) return null;

        var providers = await connection.QueryAsync<string>(
            """
            SELECT provider_key
            FROM sol_external_identity
            WHERE account_id = @AccountId
            ORDER BY created_at
            """,
            new AccountIdArgs { AccountId = accountId.Value },
            transaction);

        return new AccountProfile(
            new AccountId(row.AccountId),
            row.DisplayName,
            row.Email,
            row.AvatarUrl,
            providers.ToArray());
    }

    private static async Task UpdateExternalIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            UPDATE sol_external_identity
            SET display_name = @DisplayName,
                email = @Email,
                avatar_url = @AvatarUrl,
                last_login_at = @Now
            WHERE account_id = @AccountId AND provider_key = @ProviderKey
            """,
            new ExternalIdentityUpdateArgs
            {
                AccountId = accountId.Value,
                ProviderKey = profile.ProviderKey,
                DisplayName = profile.DisplayName,
                Email = profile.Email,
                AvatarUrl = profile.AvatarUrl,
                Now = now,
            },
            transaction);

        await UpdateAccountProfileAsync(connection, accountId, profile, now, transaction);
    }

    private static async Task UpdateAccountProfileAsync(
        NpgsqlConnection connection,
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now,
        NpgsqlTransaction? transaction = null)
    {
        await connection.ExecuteAsync(
            """
            UPDATE sol_account
            SET display_name = @DisplayName,
                email = COALESCE(@Email, email),
                avatar_url = COALESCE(@AvatarUrl, avatar_url),
                updated_at = @Now
            WHERE account_id = @AccountId
            """,
            new AccountWriteArgs
            {
                AccountId = accountId.Value,
                DisplayName = profile.DisplayName,
                Email = profile.Email,
                AvatarUrl = profile.AvatarUrl,
                Now = now,
            },
            transaction);
    }
}

internal sealed class AccountRow
{
    public Guid AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
}

internal sealed class SessionLookupArgs
{
    public byte[] TokenHash { get; init; } = [];
    public DateTimeOffset Now { get; init; }
}

internal sealed class TokenHashArgs
{
    public byte[] TokenHash { get; init; } = [];
}

internal sealed class AccountIdArgs
{
    public Guid AccountId { get; init; }
}

internal sealed class DeviceIdArgs
{
    public Guid DeviceId { get; init; }
}

internal sealed class ExternalIdentityLookupArgs
{
    public string ProviderKey { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
}

internal sealed class AccountProviderArgs
{
    public Guid AccountId { get; init; }
    public string ProviderKey { get; init; } = string.Empty;
}

internal sealed class AccountWriteArgs
{
    public Guid AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
    public DateTimeOffset Now { get; init; }
}

internal sealed class ExternalIdentityWriteArgs
{
    public Guid ExternalIdentityId { get; init; }
    public Guid AccountId { get; init; }
    public string ProviderKey { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
    public DateTimeOffset Now { get; init; }
}

internal sealed class ExternalIdentityUpdateArgs
{
    public Guid AccountId { get; init; }
    public string ProviderKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
    public DateTimeOffset Now { get; init; }
}

internal sealed class SessionWriteArgs
{
    public Guid SessionId { get; init; }
    public Guid AccountId { get; init; }
    public byte[] TokenHash { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? UserAgent { get; init; }
    public string? IpAddress { get; init; }
}

internal sealed class AccountDeviceArgs
{
    public Guid AccountId { get; init; }
    public Guid DeviceId { get; init; }
    public DateTimeOffset LinkedAt { get; init; }
}

internal sealed class OAuthTransactionArgs
{
    public Guid TransactionId { get; init; }
    public byte[] StateHash { get; init; } = [];
    public string ProviderKey { get; init; } = string.Empty;
    public Guid? LinkAccountId { get; init; }
    public Guid? DeviceId { get; init; }
    public string ReturnPath { get; init; } = "/";
    public string CodeVerifier { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class OAuthConsumeArgs
{
    public byte[] StateHash { get; init; } = [];
    public DateTimeOffset Now { get; init; }
}

internal sealed class OAuthTransactionRow
{
    public Guid TransactionId { get; init; }
    public byte[] StateHash { get; init; } = [];
    public string ProviderKey { get; init; } = string.Empty;
    public Guid? LinkAccountId { get; init; }
    public Guid? DeviceId { get; init; }
    public string ReturnPath { get; init; } = "/";
    public string CodeVerifier { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }

    public OAuthTransaction ToDomain() => new(
        TransactionId,
        StateHash,
        ProviderKey,
        LinkAccountId is { } accountId ? new AccountId(accountId) : null,
        DeviceId is { } deviceId ? new DeviceId(deviceId) : null,
        ReturnPath,
        CodeVerifier,
        Utc(CreatedAt),
        Utc(ExpiresAt));

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
