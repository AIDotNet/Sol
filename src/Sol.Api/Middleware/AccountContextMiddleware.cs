using Sol.Api.Auth;
using Sol.Api.Hubs;
using Sol.Application.Abstractions.Identity;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Features.Identity;
using Sol.Domain.Identity;

namespace Sol.Api.Middleware;

/// <summary>
/// Validates the opaque account session and activates account-wide data scope only for a device
/// that the account explicitly owns.
/// </summary>
public sealed class AccountContextMiddleware(
    RequestDelegate next,
    ILogger<AccountContextMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAccountRepository accounts,
        IAccountContext accountContext,
        IHostEnvironment environment)
    {
        var rawToken = AccountSessionCookie.Read(context);
        AccountId? sessionAccountId = null;

        if (rawToken is not null)
        {
            var session = await accounts.FindBySessionTokenHashAsync(
                AccountAuthService.HashToken(rawToken),
                DateTimeOffset.UtcNow,
                context.RequestAborted);

            if (session is not null)
            {
                sessionAccountId = session.Id;
                context.Items[DeviceContextItems.SessionAccountId] = session.Id;
            }
            else
            {
                // An expired or revoked cookie is not useful and should not be sent forever.
                AccountSessionCookie.Delete(context, environment.IsDevelopment());
            }
        }

        if (DeviceCookie.Read(context) is { } deviceId)
        {
            var deviceOwner = await accounts.FindAccountForDeviceAsync(
                deviceId,
                context.RequestAborted);

            if (deviceOwner is { } owner)
            {
                if (sessionAccountId == owner)
                {
                    accountContext.AccountId = owner;
                    context.Items[DeviceContextItems.AccountId] = owner;
                }
                else
                {
                    // The device cookie alone can still identify a guest, but once it is claimed
                    // by an account it must not bypass the account session on another browser.
                    context.Items[DeviceContextItems.DeviceAccessBlocked] = true;
                    logger.LogDebug(
                        "Blocked account-owned device {DeviceId} without its matching account session.",
                        deviceId.Value);
                }
            }
        }

        await next(context);
    }
}

public static class AccountContextMiddlewareExtensions
{
    public static IApplicationBuilder UseAccountContext(this IApplicationBuilder app) =>
        app.UseMiddleware<AccountContextMiddleware>();
}
