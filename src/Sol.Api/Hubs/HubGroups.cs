using Sol.Domain.Identity;

namespace Sol.Api.Hubs;

/// <summary>
/// Group naming. Groups are backplane-aware, so this addressing scheme works identically on a
/// single node and across a Redis-backed cluster.
/// </summary>
public static class HubGroups
{
    /// <summary>One browser profile — typically several connections, one per open tab.</summary>
    public static string ForDevice(DeviceId deviceId) => $"device:{deviceId.Value:N}";

    /// <summary>
    /// Every device believed to be the same physical machine. Membership beyond the first
    /// device is probabilistic, so avoid sending anything sensitive to this group.
    /// </summary>
    public static string ForVisitor(VisitorId visitorId) => $"visitor:{visitorId.Value:N}";
}

/// <summary>
/// Client-side method names.
/// </summary>
/// <remarks>
/// Kept in one place because Native AOT rules out strongly typed hubs: without them the compiler
/// cannot check these names, so a typo would fail silently at runtime.
/// </remarks>
public static class RealtimeMethods
{
    public const string ReceiveMessage = "ReceiveMessage";
    public const string DeviceLinked = "DeviceLinked";
}

/// <summary>Keys under which per-request identity is stashed in <c>HttpContext.Items</c>.</summary>
public static class DeviceContextItems
{
    public const string DeviceId = "sol.device_id";
    public const string VisitorId = "sol.visitor_id";
    public const string AccountId = "sol.account_id";
    public const string SessionAccountId = "sol.session_account_id";
    public const string DeviceAccessBlocked = "sol.device_access_blocked";
}
