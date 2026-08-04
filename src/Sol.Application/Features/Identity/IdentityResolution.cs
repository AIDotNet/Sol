using Sol.Domain.Identity;

namespace Sol.Application.Features.Identity;

/// <summary>Outcome of resolving a request to a device and visitor.</summary>
public sealed record IdentityResolution(
    DeviceId DeviceId,
    VisitorId VisitorId,
    bool IsNewDevice,
    LinkConfidence Confidence,
    LinkMethod Method)
{
    /// <summary>
    /// Whether the caller may use this identity to gate an irreversible decision. False for a
    /// probabilistic cross-browser guess, which should at most add friction.
    /// </summary>
    public bool IsTrustworthy => Method != LinkMethod.ProbabilisticCoarse;
}
