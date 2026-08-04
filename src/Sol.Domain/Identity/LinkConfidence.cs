namespace Sol.Domain.Identity;

/// <summary>
/// How much the system trusts a device-to-visitor association.
/// </summary>
/// <remarks>
/// Probabilistic links are capped at <see cref="MaxProbabilistic"/> deliberately. Cross-browser
/// matching relies on signals (timezone, CPU count, screen, OS) that carry only ~10-15 bits of
/// correlated entropy, so a match is evidence, never proof. Business rules should treat anything
/// below <see cref="Certain"/> as grounds for extra friction (a captcha, a confirmation) rather
/// than for a hard allow or deny.
/// </remarks>
public readonly record struct LinkConfidence
{
    /// <summary>Ceiling for any probabilistic association.</summary>
    public const double MaxProbabilistic = 0.6d;

    private LinkConfidence(double value) => Value = value;

    public double Value { get; }

    /// <summary>A cookie or stored-id match. The only tier that may gate a hard decision.</summary>
    public static LinkConfidence Certain => new(1.0d);

    /// <summary>A client-stored id restored after the cookie was cleared — same browser.</summary>
    public static LinkConfidence StorageRestore => new(0.95d);

    /// <summary>A coarse fingerprint plus IP-prefix match. A hint only.</summary>
    public static LinkConfidence Probabilistic => new(MaxProbabilistic);

    public static LinkConfidence For(LinkMethod method) => method switch
    {
        LinkMethod.Deterministic => Certain,
        LinkMethod.ProbabilisticCoarse => Probabilistic,
        LinkMethod.Manual => Certain,
        _ => Probabilistic,
    };

    /// <summary>
    /// Clamps an arbitrary value into the valid range, additionally holding probabilistic
    /// links to <see cref="MaxProbabilistic"/> so a caller cannot inflate a guess into a fact.
    /// </summary>
    public static LinkConfidence Create(double value, LinkMethod method)
    {
        var clamped = Math.Clamp(value, 0.01d, 1.0d);

        if (method == LinkMethod.ProbabilisticCoarse && clamped > MaxProbabilistic)
        {
            clamped = MaxProbabilistic;
        }

        return new LinkConfidence(clamped);
    }

    public override string ToString() => Value.ToString("0.00");
}
