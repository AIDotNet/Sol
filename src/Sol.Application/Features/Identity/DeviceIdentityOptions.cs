namespace Sol.Application.Features.Identity;

/// <summary>Tuning for device identity resolution.</summary>
public sealed class DeviceIdentityOptions
{
    public const string SectionName = "DeviceIdentity";

    /// <summary>
    /// Server-side secret mixed into every fingerprint hash, so stored fingerprints cannot be
    /// recomputed from public signal values or matched against another system's database.
    /// Supply via user-secrets or an environment variable — never appsettings.json.
    /// </summary>
    public string Pepper { get; set; } = string.Empty;

    /// <summary>
    /// Which normalization scheme to hash with. Bump when the signal set changes; old rows keep
    /// their recorded version so both schemes can coexist during a rollout.
    /// </summary>
    public int SignalVersion { get; set; } = 1;

    /// <summary>How recently a candidate must have been seen to be considered the same device.</summary>
    public int CoarseWindowHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of devices that may share a coarse fingerprint before the match is treated
    /// as meaningless. This ceiling is load-bearing: the browser-independent signals carry only
    /// ~10-15 bits of correlated entropy, so an office behind one NAT will collapse dozens of
    /// unrelated people onto a single (fingerprint, ip_prefix) pair. Without the cap, they would
    /// all be welded into one visitor. A weak fingerprint may only link when it is also rare.
    /// </summary>
    public int MaxCoarseFanout { get; set; } = 5;

    /// <summary>Cookie name carrying the deterministic device id.</summary>
    public string CookieName { get; set; } = "sol_did";

    public int CookieMaxAgeDays { get; set; } = 730;
}
