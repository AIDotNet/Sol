using Sol.Application.Contracts.Device;

namespace Sol.Application.Abstractions.Security;

/// <summary>
/// Turns raw browser signals into the two fingerprints the identity resolver matches on.
/// </summary>
public interface IFingerprintHasher
{
    /// <summary>
    /// Hash over browser-independent signals plus the IP prefix. The only fingerprint that can
    /// match across browsers — and weak, because those signals carry little entropy.
    /// </summary>
    byte[] ComputeCoarse(StableSignals? stable, string ipPrefix, int signalVersion);

    /// <summary>
    /// Hash over all signals. Matching values effectively identify the same browser profile.
    /// </summary>
    byte[] ComputeExact(StableSignals? stable, VolatileSignals? volatileSignals, int signalVersion);

    /// <summary>Canonical JSON of the normalized signals, persisted so drift can be re-analysed later.</summary>
    string CanonicalizeForStorage(DeviceSignalsPayload payload);
}
