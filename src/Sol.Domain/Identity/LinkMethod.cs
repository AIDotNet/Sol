namespace Sol.Domain.Identity;

/// <summary>How a device came to be associated with a visitor.</summary>
public enum LinkMethod
{
    /// <summary>Cookie or client-stored id matched an existing device. Trustworthy.</summary>
    Deterministic = 0,

    /// <summary>
    /// Browser-independent fingerprint plus IP prefix matched within a time window. This is a
    /// hint, not an identity — the signals involved carry only ~10-15 bits of correlated entropy.
    /// </summary>
    ProbabilisticCoarse = 1,

    /// <summary>Established by an operator.</summary>
    Manual = 2,
}
