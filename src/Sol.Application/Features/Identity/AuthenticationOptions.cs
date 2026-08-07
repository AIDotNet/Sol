namespace Sol.Application.Features.Identity;

/// <summary>Configuration for server-side OAuth and account sessions.</summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// The browser-facing origin. OAuth callbacks must use this exact origin, normally the Next.js
    /// origin rather than the internal API origin. It must be configured explicitly in production.
    /// </summary>
    public string PublicOrigin { get; set; } = "http://localhost:3000";

    public string SessionCookieName { get; set; } = "sol_session";
    public int SessionMaxAgeDays { get; set; } = 30;
    public int OAuthTransactionMinutes { get; set; } = 10;

    public GitHubAuthenticationOptions GitHub { get; set; } = new();
}

public sealed class GitHubAuthenticationOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = "read:user user:email";
}
