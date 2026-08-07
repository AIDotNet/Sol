using Sol.Api.Hubs;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Identity;

namespace Sol.Api.Middleware;

/// <summary>
/// Resolves the device cookie into <c>HttpContext.Items</c> and the logging scope.
/// </summary>
/// <remarks>
/// Read-only: it never issues a cookie or creates a device. Identity is minted solely by the
/// handshake endpoint, so a request arriving without a cookie simply carries no identity rather
/// than silently creating one on, say, a health probe or a crawler request.
/// </remarks>
public sealed class DeviceContextMiddleware(RequestDelegate next, ILogger<DeviceContextMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IVisitorRepository visitors)
    {
        if (DeviceCookie.Read(context) is { } deviceId)
        {
            context.Items[DeviceContextItems.DeviceId] = deviceId;

            var visitorId = await visitors.FindVisitorForDeviceAsync(deviceId, context.RequestAborted);
            if (visitorId is { } visitor)
            {
                context.Items[DeviceContextItems.VisitorId] = visitor;
            }

            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       ["device_id"] = deviceId.Value,
                       ["visitor_id"] = visitorId?.Value ?? Guid.Empty,
                   }))
            {
                await next(context);
                return;
            }
        }

        await next(context);
    }
}

public static class DeviceContextMiddlewareExtensions
{
    public static IApplicationBuilder UseDeviceContext(this IApplicationBuilder app) =>
        app.UseMiddleware<DeviceContextMiddleware>();

    /// <summary>Identity resolved for the current request, if the caller presented a cookie.</summary>
    public static DeviceId? GetDeviceId(this HttpContext context) =>
        context.Items[DeviceContextItems.DeviceAccessBlocked] is true
            ? null
            : context.Items[DeviceContextItems.DeviceId] is DeviceId deviceId
                ? deviceId
                : null;

    public static VisitorId? GetVisitorId(this HttpContext context) =>
        context.Items[DeviceContextItems.VisitorId] is VisitorId visitorId
            ? visitorId
            : null;

    /// <summary>Returns the validated account session, even before its new device is linked.</summary>
    public static AccountId? GetAuthenticatedAccountId(this HttpContext context) =>
        context.Items[DeviceContextItems.SessionAccountId] is AccountId accountId
            ? accountId
            : null;

    /// <summary>
    /// Returns the account scope used by repositories. It is set only when the current device is
    /// already linked to the validated session account.
    /// </summary>
    public static AccountId? GetAccountId(this HttpContext context) =>
        context.Items[DeviceContextItems.AccountId] is AccountId accountId
            ? accountId
            : null;
}
