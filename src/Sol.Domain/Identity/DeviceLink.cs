namespace Sol.Domain.Identity;

/// <summary>
/// An edge associating a device with a visitor.
/// </summary>
/// <remarks>
/// Links are stored as reversible edges and never as a destructive merge of rows. A wrong
/// probabilistic guess must cost one DELETE, not a data-recovery exercise — which matters
/// because on shared NAT egress a coarse fingerprint will collide across unrelated people.
/// </remarks>
public sealed record DeviceLink(
    DeviceId DeviceId,
    VisitorId VisitorId,
    LinkConfidence Confidence,
    LinkMethod Method,
    DateTimeOffset CreatedAt);
