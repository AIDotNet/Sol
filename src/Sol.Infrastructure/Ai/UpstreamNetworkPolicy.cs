using System.Net;
using System.Net.Sockets;
using Sol.Infrastructure.Security;

namespace Sol.Infrastructure.Ai;

/// <summary>
/// Connect-time guard for the shared <c>"upstream"</c> HTTP client.
/// </summary>
/// <remarks>
/// Provider base URLs and the download links an upstream hands back are user-influenced
/// destinations, so the connection itself is the last line of defence against SSRF: this
/// callback resolves the host, refuses the whole set when any address falls into a blocked
/// range, and then connects only to the addresses that were just validated — closing the
/// DNS-rebinding window in the same step. Redirects are followed by the handler, so every hop
/// passes through here too.
/// </remarks>
internal static class UpstreamNetworkPolicy
{
    internal static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>
        ConnectCallback(bool allowPrivateNetworks) =>
        async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, ct);
                }
                catch (SocketException exception)
                {
                    throw new HttpRequestException($"Could not resolve the upstream host '{host}'.", exception);
                }
            }

            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"The upstream host '{host}' resolved to no addresses.");
            }

            if (!allowPrivateNetworks && addresses.Any(a => PrivateNetworkPolicy.IsForbidden(a)))
            {
                // Deliberately generic: the error is reported back to the user who configured
                // the URL, and must not become a tool for mapping the server's internals.
                throw new HttpRequestException(
                    "The upstream host resolves to a blocked address (loopback, private, or reserved network).");
            }

            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception exception)
                {
                    last = exception;
                    socket.Dispose();
                    if (exception is OperationCanceledException) throw;
                }
            }

            throw new HttpRequestException(
                $"Could not connect to the upstream host '{host}'.", last);
        };
}
