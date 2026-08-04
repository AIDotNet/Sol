namespace Sol.Application.Contracts.Device;

/// <summary>
/// Signals collected by the browser and posted to the handshake endpoint.
/// </summary>
/// <remarks>
/// Every field is optional. A privacy-hardened browser (Safari 26 protects screen metrics,
/// hardwareConcurrency, canvas and WebAudio by default) should still produce a stable —
/// if lower-entropy — fingerprint rather than an error, so missing values normalize to a
/// sentinel instead of failing validation.
/// </remarks>
public sealed record DeviceSignalsPayload
{
    /// <summary>Signal-set version the client collected against.</summary>
    public int V { get; init; } = 1;

    public StableSignals? Stable { get; init; }

    public VolatileSignals? Volatile { get; init; }

    /// <summary>
    /// The device id previously mirrored into localStorage, if any. Lets a browser that lost
    /// its cookie reclaim the same identity. Null on a genuine first visit.
    /// </summary>
    public string? ClientStoredId { get; init; }
}

/// <summary>
/// Signals that are properties of the hardware or OS rather than the browser, and so stand a
/// chance of matching across browsers on one machine. Their combined entropy is low.
/// </summary>
public sealed record StableSignals
{
    public string? TimeZone { get; init; }
    public string? Platform { get; init; }
    public int? HardwareConcurrency { get; init; }
    public double? DeviceMemoryGb { get; init; }
    public ScreenSignals? Screen { get; init; }
    public GpuSignals? Gpu { get; init; }
    public string? PrimaryLanguage { get; init; }
}

public sealed record ScreenSignals
{
    public int? W { get; init; }
    public int? H { get; init; }
    public int? ColorDepth { get; init; }
    public double? Dpr { get; init; }
}

public sealed record GpuSignals
{
    public string? Vendor { get; init; }
    public string? Renderer { get; init; }
}

/// <summary>
/// Browser-dependent signals. High entropy, but different engines rasterize fonts and render
/// audio differently, so these never match across browsers — they identify a browser, not a device.
/// </summary>
public sealed record VolatileSignals
{
    public string? UserAgent { get; init; }
    public IReadOnlyList<string>? Languages { get; init; }
    public string? CanvasHash { get; init; }
    public string? AudioHash { get; init; }
    public string? FontsHash { get; init; }
}
