using System.Net;
using System.Net.Sockets;

namespace Sol.Infrastructure.Security;

/// <summary>
/// Central policy for addresses an outbound request may connect to.
/// </summary>
/// <remarks>
/// User-supplied destinations (provider base URLs, MCP server URLs, download links returned by
/// an upstream) must never reach loopback, link-local, private, or otherwise reserved ranges:
/// any of those turns a user-controlled URL into a probe of the server's own network. The check
/// runs on the <see cref="IPAddress"/> at connect time, not on the hostname, so DNS answers that
/// change between validation and connection (DNS rebinding) cannot bypass it.
/// </remarks>
internal static class PrivateNetworkPolicy
{
    // 64:ff9b::/96 — the well-known NAT64 prefix embeds an IPv4 address in the low 32 bits.
    private static ReadOnlySpan<byte> Nat64Prefix => [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    internal static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsForbidden(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => true,
                100 when bytes[1] is >= 64 and <= 127 => true, // CGNAT 100.64.0.0/10
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                >= 224 => true, // multicast and reserved
                _ => false,
            };
        }

        // fc00::/7 unique-local, fe80::/10 link-local, ff00::/8 multicast.
        if ((bytes[0] & 0xfe) == 0xfc
            || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            || bytes[0] == 0xff)
        {
            return true;
        }

        // IPv4-compatible (::/96, deprecated) and NAT64 (64:ff9b::/96): the low 32 bits are an
        // IPv4 address that the host may route straight to a private destination.
        if (bytes.AsSpan(0, 12).SequenceEqual(Nat64Prefix)
            || bytes.AsSpan(0, 12).SequenceEqual(stackalloc byte[12]))
        {
            return IsForbidden(EmbeddedIPv4(bytes));
        }

        // 6to4 (2002::/16): the first 32 bits after the prefix are a public IPv4 address.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return IsForbidden(new IPAddress(bytes.AsSpan(2, 4).ToArray()));
        }

        return false;
    }

    private static IPAddress EmbeddedIPv4(byte[] bytes) => new(bytes.AsSpan(12, 4).ToArray());
}
