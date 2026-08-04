namespace Sol.Domain.Identity;

/// <summary>
/// Identifies a presumed physical device — a cluster of one or more <see cref="DeviceId"/>s.
/// Membership beyond the first device is probabilistic, so a visitor is a hypothesis rather
/// than a fact. See <see cref="DeviceLink"/> for the confidence attached to each membership.
/// </summary>
public readonly record struct VisitorId(Guid Value)
{
    public static VisitorId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out VisitorId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new VisitorId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
