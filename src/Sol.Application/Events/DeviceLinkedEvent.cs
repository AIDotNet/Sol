namespace Sol.Application.Events;

/// <summary>
/// Raised when a device is associated with a visitor. Consumers should read
/// <paramref name="Method"/> before acting: a probabilistic link is a hint, not a fact.
/// </summary>
public sealed record DeviceLinkedEvent(
    string DeviceId,
    string VisitorId,
    double Confidence,
    string Method,
    DateTimeOffset OccurredAt)
{
    public const string RoutingKey = "device.linked";
}
