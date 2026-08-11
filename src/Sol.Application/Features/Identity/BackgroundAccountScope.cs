using Sol.Application.Abstractions.Identity;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Application.Features.Identity;

/// <summary>
/// Restores account access for durable work running outside the HTTP request that created it.
/// </summary>
/// <remarks>
/// The account is resolved from the device's current durable ownership instead of being copied
/// from a request. A removed device link therefore takes effect immediately. Assigning the null
/// result is equally important: one scoped background worker may process jobs from several accounts
/// and must not retain the previous job's access.
/// </remarks>
public sealed class BackgroundAccountScope(
    IAccountRepository accounts,
    IAccountContext accountContext)
{
    public async Task RestoreForDeviceAsync(DeviceId deviceId, CancellationToken ct)
    {
        accountContext.AccountId = await accounts.FindAccountForDeviceAsync(deviceId, ct);
    }
}
