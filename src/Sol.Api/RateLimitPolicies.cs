namespace Sol.Api;

/// <summary>
/// Names of the per-IP rate-limit policies registered in <c>Program</c> and attached to
/// endpoints with <c>RequireRateLimiting</c>.
/// </summary>
/// <remarks>
/// The two buckets match two abuse shapes: <see cref="Identity"/> covers endpoints reachable
/// with no prior credential that each create rows (handshake, OAuth start), and
/// <see cref="ConfigWrite"/> covers configuration writes reachable with only a device cookie
/// (providers, MCP servers, skills, config imports) plus the provider/MCP checks that make the
/// server call out to a user-supplied host.
/// </remarks>
public static class RateLimitPolicies
{
    public const string Identity = "identity";

    public const string ConfigWrite = "config-write";
}
