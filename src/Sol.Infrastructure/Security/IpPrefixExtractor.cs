using System.Net;
using System.Net.Sockets;
using Sol.Application.Abstractions.Security;

namespace Sol.Infrastructure.Security;

/// <inheritdoc />
public sealed class IpPrefixExtractor : IIpPrefixExtractor
{
    private const string Unknown = "0.0.0.0";

    public string Extract(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        // An IPv4-mapped IPv6 address (::ffff:192.0.2.1) is really IPv4; masking it as /48
        // would keep the whole address and defeat the point.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => MaskIPv4(address),
            AddressFamily.InterNetworkV6 => MaskIPv6(address),
            _ => Unknown,
        };
    }

    /// <summary>/24 — keeps "same local network" while surviving a DHCP lease change.</summary>
    private static string MaskIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        bytes[3] = 0;
        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// /48 — the usual site allocation boundary. Anything narrower would drift as SLAAC
    /// and privacy extensions rotate the host portion.
    /// </summary>
    private static string MaskIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 6; i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        return new IPAddress(bytes).ToString();
    }
}
