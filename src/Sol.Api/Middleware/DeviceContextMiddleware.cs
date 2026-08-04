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
        context.Items[DeviceContextItems.DeviceId] as DeviceId?;

    public static VisitorId? GetVisitorId(this HttpContext context) =>
        context.Items[DeviceContextItems.VisitorId] as VisitorId?;
}
