using System.Net;
using System.Net.Sockets;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Ai.Mcp;

internal interface IMcpNetworkGuard
{
    ValueTask<IReadOnlyList<IPAddress>> ValidateAsync(Uri uri, CancellationToken ct);
}

/// <summary>Rejects MCP URLs that can reach local infrastructure or change address after validation.</summary>
internal sealed class McpNetworkGuard(McpOptions options) : IMcpNetworkGuard
{
    public async ValueTask<IReadOnlyList<IPAddress>> ValidateAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("MCP URL must be absolute and cannot contain user info.");
        }
        if (uri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("MCP URL must use HTTPS.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException("The MCP host could not be resolved.", exception);
        }
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException("The MCP host resolved to no addresses.");
        }

        var allLoopback = addresses.All(IPAddress.IsLoopback);
        if (uri.Scheme == "http" && (!options.AllowLoopbackHttp || !allLoopback))
        {
            throw new InvalidOperationException(
                "Plain HTTP MCP is allowed only for loopback hosts in development.");
        }

        if (!allLoopback && addresses.Any(IsForbidden))
        {
            throw new InvalidOperationException("The MCP host resolves to a private or reserved address.");
        }
        if (allLoopback && !options.AllowLoopbackHttp)
        {
            throw new InvalidOperationException("Loopback MCP is disabled outside development.");
        }

        return addresses;
    }

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
                100 when bytes[1] is >= 64 and <= 127 => true, // CGNAT
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                >= 224 => true, // multicast and reserved
                _ => false,
            };
        }

        // fc00::/7 unique-local, fe80::/10 link-local, ff00::/8 multicast.
        return (bytes[0] & 0xfe) == 0xfc
            || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            || bytes[0] == 0xff;
    }
}
