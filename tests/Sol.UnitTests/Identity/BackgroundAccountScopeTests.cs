using Sol.Application.Abstractions.Identity;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Features.Identity;
using Sol.Domain.Identity;

namespace Sol.UnitTests.Identity;

public class BackgroundAccountScopeTests
{
    [Fact]
    public async Task RestoreTracksCurrentDeviceOwnershipAndClearsPreviousAccount()
    {
        var accountId = AccountId.New();
        var accountDevice = DeviceId.New();
        var guestDevice = DeviceId.New();
        var repository = new AccountRepositoryStub(
            new Dictionary<DeviceId, AccountId> { [accountDevice] = accountId });
        var context = new AccountContextStub { AccountId = AccountId.New() };
        var scope = new BackgroundAccountScope(repository, context);

        await scope.RestoreForDeviceAsync(
            accountDevice, TestContext.Current.CancellationToken);
        Assert.Equal(accountId, context.AccountId);

        await scope.RestoreForDeviceAsync(
            guestDevice, TestContext.Current.CancellationToken);
        Assert.Null(context.AccountId);
        Assert.Equal([accountDevice, guestDevice], repository.LookedUpDevices);
    }

    private sealed class AccountContextStub : IAccountContext
    {
        public AccountId? AccountId { get; set; }
    }

    private sealed class AccountRepositoryStub(
        IReadOnlyDictionary<DeviceId, AccountId> accounts) : IAccountRepository
    {
        public List<DeviceId> LookedUpDevices { get; } = [];

        public Task<AccountId?> FindAccountForDeviceAsync(DeviceId deviceId, CancellationToken ct)
        {
            LookedUpDevices.Add(deviceId);
            return Task.FromResult<AccountId?>(
                accounts.TryGetValue(deviceId, out var accountId) ? accountId : null);
        }

        public Task<AccountProfile?> FindBySessionTokenHashAsync(
            byte[] tokenHash, DateTimeOffset now, CancellationToken ct) => Unsupported<AccountProfile?>();

        public Task<AccountProfile?> FindByIdAsync(AccountId accountId, CancellationToken ct) =>
            Unsupported<AccountProfile?>();

        public Task<AccountId?> FindByExternalIdentityAsync(
            string providerKey, string subject, CancellationToken ct) => Unsupported<AccountId?>();

        public Task<AccountProfile> GetOrCreateFromExternalAsync(
            ExternalLoginProfile profile, DateTimeOffset now, CancellationToken ct) =>
            Unsupported<AccountProfile>();

        public Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
            AccountId accountId,
            ExternalLoginProfile profile,
            DateTimeOffset now,
            CancellationToken ct) => Unsupported<ExternalIdentityLinkResult>();

        public Task CreateSessionAsync(AccountSession session, CancellationToken ct) => Unsupported();

        public Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct) => Unsupported();

        public Task<bool> LinkDeviceAsync(
            AccountId accountId, DeviceId deviceId, CancellationToken ct) => Unsupported<bool>();

        public Task CreateOAuthTransactionAsync(
            OAuthTransaction transaction, CancellationToken ct) => Unsupported();

        public Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
            byte[] stateHash, DateTimeOffset now, CancellationToken ct) =>
            Unsupported<OAuthTransaction?>();

        private static Task Unsupported() =>
            Task.FromException(new NotSupportedException());

        private static Task<T> Unsupported<T>() =>
            Task.FromException<T>(new NotSupportedException());
    }
}
