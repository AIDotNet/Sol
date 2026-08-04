using Microsoft.Extensions.Options;
using Sol.Application.Features.Identity;
using Sol.Domain.Identity;

namespace Sol.Api.Hubs;

/// <summary>
/// Reads and writes the device cookie.
/// </summary>
/// <remarks>
/// The cookie is <c>HttpOnly</c>, so JavaScript cannot read it — which is why the handshake also
/// returns the device id in its response body for the client to mirror into localStorage. Two
/// independent channels carrying the same value: the cookie survives a localStorage clear, and
/// localStorage survives a cookie clear.
/// </remarks>
public static class DeviceCookie
{
    private static string _cookieName = "sol_did";
    private static int _maxAgeDays = 730;

    public static void Configure(IOptions<DeviceIdentityOptions> options)
    {
        _cookieName = options.Value.CookieName;
        _maxAgeDays = options.Value.CookieMaxAgeDays;
    }

    public static DeviceId? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(_cookieName, out var raw) &&
        DeviceId.TryParse(raw, out var deviceId)
            ? deviceId
            : null;

    public static void Write(HttpContext context, DeviceId deviceId, bool isDevelopment)
    {
        context.Response.Cookies.Append(_cookieName, deviceId.Value.ToString(), new CookieOptions
        {
            HttpOnly = true,
            // SameSite=Lax requires the API and the site to share an origin. In development the
            // Next.js app proxies /api and /hubs so the browser only ever sees one origin; in
            // production both sit behind the same reverse proxy.
            SameSite = SameSiteMode.Lax,
            // Secure cookies are rejected over plain HTTP, which is what local development uses.
            Secure = !isDevelopment,
            Path = "/",
            MaxAge = TimeSpan.FromDays(_maxAgeDays),
            IsEssential = true,
        });
    }
}
