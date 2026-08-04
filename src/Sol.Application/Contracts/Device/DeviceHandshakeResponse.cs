namespace Sol.Application.Contracts.Device;

/// <summary>
/// Identity returned from the handshake.
/// </summary>
/// <param name="DeviceId">
/// Also delivered as an HttpOnly cookie. It is repeated in the body because JavaScript cannot
/// read an HttpOnly cookie — the client takes the value from here to mirror into localStorage.
/// </param>
/// <param name="Confidence">
/// How the identity was established: 1.0 for a cookie match, 0.95 for a localStorage restore,
/// 0.6 for a cross-browser guess. Do not gate a hard allow/deny on anything below 1.0.
/// </param>
public sealed record DeviceHandshakeResponse(
    string DeviceId,
    string VisitorId,
    bool IsNewDevice,
    double Confidence,
    string Method);
