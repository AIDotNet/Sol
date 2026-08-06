using System.Net;
using System.Net.Sockets;
using System.Text;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Ai.Mcp;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Security;

namespace Sol.UnitTests.Ai;

public class McpRuntimeTests
{
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.2.3")]
    [InlineData("192.168.1.4")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public void PrivateAndReservedAddressesAreForbidden(string text)
    {
        Assert.True(McpNetworkGuard.IsForbidden(IPAddress.Parse(text)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAllowed(string text)
    {
        Assert.False(McpNetworkGuard.IsForbidden(IPAddress.Parse(text)));
    }

    [Fact]
    public async Task StreamableHttpInitializesAndListsTools()
    {
        await using var server = await LoopbackMcpServer.StartAsync();
        var runtime = Runtime();

        var tools = await runtime.ListToolsAsync(
            Registration(server.Url), TestContext.Current.CancellationToken);

        var tool = Assert.Single(tools);
        Assert.Equal("search", tool.Name);
        Assert.Equal("Search things", tool.Description);
        Assert.Equal("{\"type\":\"object\"}", tool.InputSchemaJson);
        await server.Completion;
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], server.Methods);
    }

    [Fact]
    public async Task StreamableHttpCallsAToolAndReturnsStructuredContent()
    {
        await using var server = await LoopbackMcpServer.StartAsync();
        var runtime = Runtime();

        var result = await runtime.CallToolAsync(
            Registration(server.Url), "search", "{\"q\":\"cats\"}",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("cats found", result.Json, StringComparison.Ordinal);
        await server.Completion;
        Assert.Equal(["initialize", "notifications/initialized", "tools/call"], server.Methods);
    }

    [Fact]
    public async Task LegacySseDiscoversItsMessageEndpointAndListsTools()
    {
        await using var server = await LegacySseMcpServer.StartAsync();
        var runtime = Runtime();

        var tools = await runtime.ListToolsAsync(
            Registration(server.Url) with { Transport = McpTransport.Sse },
            TestContext.Current.CancellationToken);

        Assert.Equal("search", Assert.Single(tools).Name);
        await server.Completion;
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], server.Methods);
    }

    [Fact]
    public async Task PlainHttpIsRejectedWithoutDevelopmentLoopbackOption()
    {
        var options = new McpOptions { AllowLoopbackHttp = false };
        var guard = new McpNetworkGuard(options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await guard.ValidateAsync(
                new Uri("http://127.0.0.1:1234/mcp"),
                TestContext.Current.CancellationToken));

        Assert.Contains("Plain HTTP", exception.Message, StringComparison.Ordinal);
    }

    private static McpRuntime Runtime()
    {
        var options = new McpOptions
        {
            AllowLoopbackHttp = true,
            RequestTimeoutSeconds = 10,
            MaxResponseBytes = 65_536,
        };
        var protector = new AesGcmApiKeyProtector(new AiOptions { EncryptionKey = TestKey });
        return new McpRuntime(protector, new McpNetworkGuard(options), options);
    }

    private static McpServer Registration(string url) => new(
        McpServerId.New(), DeviceId.New(), "test", null, true,
        McpTransport.StreamableHttp, null, "[]", "{}", null, url, "{}", false,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class LegacySseMcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        private LegacySseMcpServer(TcpListener listener)
        {
            _listener = listener;
            Completion = ServeAsync(_stop.Token);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/sse";
        public List<string> Methods { get; } = [];
        public Task Completion { get; }

        public static Task<LegacySseMcpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LegacySseMcpServer(listener));
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            using var sseClient = await _listener.AcceptTcpClientAsync(ct);
            await using var sse = sseClient.GetStream();
            using (var reader = new StreamReader(sse, Encoding.UTF8, false, leaveOpen: true))
            {
                while (await reader.ReadLineAsync(ct) is { } line && line.Length > 0) { }
            }
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: keep-alive\r\n\r\n");
            await sse.WriteAsync(headers, ct);
            var endpoint = Encoding.UTF8.GetBytes(
                $"event: endpoint\ndata: /message\n\n");
            await sse.WriteAsync(endpoint, ct);
            await sse.FlushAsync(ct);

            for (var index = 0; index < 3; index++)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                var body = await ReadBodyAsync(stream, ct);
                var method = ReadJsonRpcMethod(body);
                Methods.Add(method);
                await WriteHttpAsync(stream, 202, string.Empty, ct);

                string? response = method switch
                {
                    "initialize" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}",
                    "tools/list" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"search\",\"description\":\"Search things\",\"inputSchema\":{\"type\":\"object\"}}]}}",
                    _ => null,
                };
                if (response is not null)
                {
                    var frame = Encoding.UTF8.GetBytes($"event: message\ndata: {response}\n\n");
                    await sse.WriteAsync(frame, ct);
                    await sse.FlushAsync(ct);
                }
            }
        }

        private static async Task<string> ReadBodyAsync(Stream stream, CancellationToken ct)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
            var length = 0;
            while (await reader.ReadLineAsync(ct) is { } line && line.Length > 0)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line["Content-Length:".Length..].Trim());
            }
            var chars = new char[length];
            var read = 0;
            while (read < chars.Length)
            {
                var count = await reader.ReadAsync(chars.AsMemory(read), ct);
                if (count == 0) break;
                read += count;
            }
            return new string(chars, 0, read);
        }

        private static string ReadJsonRpcMethod(string body)
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.GetProperty("method").GetString() ?? string.Empty;
        }

        private static async Task WriteHttpAsync(
            Stream stream,
            int status,
            string body,
            CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} Accepted\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            if (bytes.Length > 0) await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await Completion; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private sealed class LoopbackMcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        private LoopbackMcpServer(TcpListener listener)
        {
            _listener = listener;
            Completion = ServeAsync(_stop.Token);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp";
        public List<string> Methods { get; } = [];
        public Task Completion { get; }

        public static Task<LoopbackMcpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackMcpServer(listener));
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            for (var index = 0; index < 3; index++)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(ct) is { } line && line.Length > 0)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                }
                var chars = new char[contentLength];
                var read = 0;
                while (read < chars.Length)
                {
                    var count = await reader.ReadAsync(chars.AsMemory(read), ct);
                    if (count == 0) break;
                    read += count;
                }
                var body = new string(chars, 0, read);
                var method = ReadMethod(body);
                Methods.Add(method);

                if (method == "notifications/initialized")
                {
                    await WriteAsync(stream, 202, string.Empty, ct);
                    continue;
                }

                var response = method switch
                {
                    "initialize" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"test\",\"version\":\"1\"}}}",
                    "tools/list" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"search\",\"description\":\"Search things\",\"inputSchema\":{\"type\":\"object\"}}]}}",
                    "tools/call" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"cats found\"}],\"isError\":false}}",
                    _ => throw new InvalidOperationException($"Unexpected method {method}"),
                };
                await WriteAsync(stream, 200, response, ct);
            }
        }

        private static string ReadMethod(string body)
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.GetProperty("method").GetString() ?? string.Empty;
        }

        private static async Task WriteAsync(
            Stream stream,
            int status,
            string body,
            CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : "Accepted";
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, ct);
            if (bytes.Length > 0) await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await Completion; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
