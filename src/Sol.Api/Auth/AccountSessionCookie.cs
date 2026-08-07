using Sol.Application.Features.Identity;

namespace Sol.Api.Auth;

/// <summary>
/// Stores only an opaque random session token in the browser. The token's SHA-256 hash is what is
/// persisted in PostgreSQL, which keeps a database leak from becoming an immediately replayable
/// login.
/// </summary>
public static class AccountSessionCookie
{
    private static string cookieName = "sol_session";
    private static int maxAgeDays = 30;

    public static void Configure(AuthenticationOptions options)
    {
        cookieName = options.SessionCookieName;
        maxAgeDays = options.SessionMaxAgeDays;
    }

    public static string? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(cookieName, out var raw) &&
        !string.IsNullOrWhiteSpace(raw)
            ? raw
            : null;

    public static void Write(HttpContext context, string token, bool isDevelopment)
    {
        context.Response.Cookies.Append(cookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !isDevelopment,
            Path = "/",
            MaxAge = TimeSpan.FromDays(maxAgeDays),
            IsEssential = true,
        });
    }

    public static void Delete(HttpContext context, bool isDevelopment)
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !isDevelopment,
            Path = "/",
        });
    }
}
