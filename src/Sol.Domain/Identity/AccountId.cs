namespace Sol.Domain.Identity;

/// <summary>
/// Identifies a signed-in Sol account. Account identity is deliberately separate from a
/// <see cref="DeviceId"/>: a guest browser can become an account device without changing the
/// ownership keys already used by the existing data model.</summary>
public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out AccountId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new AccountId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
