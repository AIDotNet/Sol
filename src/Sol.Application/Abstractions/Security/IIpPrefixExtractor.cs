using System.Net;

namespace Sol.Application.Abstractions.Security;

/// <summary>
/// Reduces a client address to a network prefix — /24 for IPv4, /48 for IPv6.
/// </summary>
/// <remarks>
/// Storing a prefix rather than the full address keeps the coarse fingerprint stable across a
/// DHCP lease change while retaining "same network" as a signal, and narrows how much personal
/// data is retained.
/// </remarks>
public interface IIpPrefixExtractor
{
    string Extract(IPAddress? address);
}
