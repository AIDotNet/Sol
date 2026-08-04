namespace Sol.Domain.Identity;

/// <summary>
/// Identifies one browser profile. This is the deterministic identifier the rest of the
/// system binds to: it is issued by the server, stored in a first-party cookie, and is
/// accurate to roughly 99% for a returning visitor in the same browser.
/// </summary>
public readonly record struct DeviceId(Guid Value)
{
    /// <summary>UUIDv7 so rows cluster by creation time in the primary key index.</summary>
    public static DeviceId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out DeviceId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new DeviceId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
