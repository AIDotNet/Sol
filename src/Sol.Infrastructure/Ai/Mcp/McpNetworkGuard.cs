using System.Net;
using System.Net.Sockets;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Security;

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

    internal static bool IsForbidden(IPAddress address) => PrivateNetworkPolicy.IsForbidden(address);
}
