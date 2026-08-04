namespace Sol.Domain.Identity;

/// <summary>
/// A browser profile the server has seen, together with the fingerprints used to recognise it.
/// </summary>
/// <param name="FingerprintExact">
/// Hash over all signals including browser-dependent ones (canvas, audio, fonts, user agent).
/// Two records sharing this value are almost certainly the same browser.
/// </param>
/// <param name="FingerprintCoarse">
/// Hash over browser-INDEPENDENT signals plus the IP prefix. This is what makes a cross-browser
/// guess possible at all, and also why that guess is weak.
/// </param>
/// <param name="SignalVersion">
/// Which normalization scheme produced the hashes. Bumping it invalidates old coarse matches,
/// which is the intended lever when browser signal availability shifts.
/// </param>
public sealed record Device(
    DeviceId Id,
    byte[] FingerprintExact,
    byte[] FingerprintCoarse,
    string IpPrefix,
    string SignalsJson,
    int SignalVersion,
    string? UserAgent,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
