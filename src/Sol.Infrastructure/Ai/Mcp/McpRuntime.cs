using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Security;
using Sol.Application.Features.Ai;
using Sol.Domain.Ai;
using Sol.Infrastructure.Ai.Protocols;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Ai.Mcp;

internal sealed class McpRuntime(
    IApiKeyProtector protector,
    IMcpNetworkGuard networkGuard,
    McpOptions options) : IMcpRuntime
{
    private const string ProtocolVersion = "2025-06-18";

    public async Task<IReadOnlyList<McpRuntimeTool>> ListToolsAsync(
        McpServer server,
        CancellationToken ct)
    {
        EnsureSupported(server);
        var result = await ExecuteSequenceAsync(server, "tools/list", new JsonObject(), ct);
        if (result["tools"] is not JsonArray tools) return [];

        var mapped = new List<McpRuntimeTool>(tools.Count);
        foreach (var node in tools)
        {
            if (node is not JsonObject tool
                || tool["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            mapped.Add(new McpRuntimeTool(
                name,
                tool["description"]?.GetValue<string>() ?? string.Empty,
                tool["inputSchema"]?.ToJsonString() ?? "{\"type\":\"object\"}"));
        }
        return mapped;
    }

    public async Task<McpRuntimeResult> CallToolAsync(
        McpServer server,
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        EnsureSupported(server);
        var arguments = ParseObject(argumentsJson);
        var result = await ExecuteSequenceAsync(
            server,
            "tools/call",
            new JsonObject { ["name"] = toolName, ["arguments"] = arguments },
            ct);
        var isError = result["isError"]?.GetValue<bool>() ?? false;
        var json = result.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > 65_536)
        {
            var preview = json.Length <= 48_000 ? json : json[..48_000];
            json = new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray((JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "MCP result exceeded 64 KiB and was truncated.\n" + preview,
                }),
            }.ToJsonString();
            isError = true;
        }
        return new McpRuntimeResult(json, isError);
    }

    private async Task<JsonObject> ExecuteSequenceAsync(
        McpServer server,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        return server.Transport switch
        {
            McpTransport.StreamableHttp => await ExecuteHttpSequenceAsync(
                server, method, parameters, deadline.Token),
            McpTransport.Sse => await ExecuteLegacySequenceAsync(
                server, method, parameters, deadline.Token),
            _ => throw new NotSupportedException("The stdio MCP transport is not supported."),
        };
    }

    private async Task<JsonObject> ExecuteHttpSequenceAsync(
        McpServer server,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        var uri = RequiredUri(server);
        using var client = await CreateClientAsync(uri, ct);
        string? sessionId = null;

        try
        {
            var initialized = await PostAsync(
                client, uri, server, Request(1, "initialize", InitializeParams()), sessionId, ct);
            sessionId = initialized.SessionId;
            ThrowIfError(initialized.Message);

            _ = await PostAsync(
                client, uri, server,
                Notification("notifications/initialized", new JsonObject()), sessionId, ct,
                expectResponse: false);

            var response = await PostAsync(
                client, uri, server, Request(2, method, parameters), sessionId, ct);
            ThrowIfError(response.Message);
            return response.Message["result"] as JsonObject ?? new JsonObject();
        }
        finally
        {
            if (sessionId is not null)
            {
                await CloseSessionAsync(client, uri, server, sessionId);
            }
        }
    }

    private async Task<JsonObject> ExecuteLegacySequenceAsync(
        McpServer server,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        var streamUri = RequiredUri(server);
        using var client = await CreateClientAsync(streamUri, ct);
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, streamUri);
        ApplyHeaders(streamRequest, server);
        streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var streamResponse = await client.SendAsync(
            streamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!streamResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP SSE connection failed ({(int)streamResponse.StatusCode}).");
        }

        await using var source = await streamResponse.Content.ReadAsStreamAsync(ct);
        await using var bounded = new BoundedReadStream(source, options.MaxResponseBytes);
        await using var enumerator = SseReader.ReadAsync(bounded, ct).GetAsyncEnumerator(ct);
        var endpoint = await ReadEndpointAsync(enumerator, streamUri, ct);
        if (!string.Equals(endpoint.Scheme, streamUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.Host, streamUri.Host, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != streamUri.Port)
        {
            throw new InvalidOperationException(
                "The MCP SSE message endpoint must use the configured origin.");
        }
        _ = await networkGuard.ValidateAsync(endpoint, ct);

        await PostLegacyAsync(client, endpoint, server, Request(1, "initialize", InitializeParams()), ct);
        var initialized = await ReadResponseAsync(enumerator, id: 1, ct);
        ThrowIfError(initialized);

        await PostLegacyAsync(
            client, endpoint, server,
            Notification("notifications/initialized", new JsonObject()), ct);
        await PostLegacyAsync(client, endpoint, server, Request(2, method, parameters), ct);
        var response = await ReadResponseAsync(enumerator, id: 2, ct);
        ThrowIfError(response);
        return response["result"] as JsonObject ?? new JsonObject();
    }

    private async Task<HttpResult> PostAsync(
        HttpClient client,
        Uri uri,
        McpServer server,
        JsonObject body,
        string? sessionId,
        CancellationToken ct,
        bool expectResponse = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, server);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP request failed ({(int)response.StatusCode}).");
        }
        var nextSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault() ?? sessionId
            : sessionId;
        if (!expectResponse || response.StatusCode == HttpStatusCode.Accepted)
        {
            return new HttpResult(new JsonObject(), nextSession);
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var bounded = new BoundedReadStream(source, options.MaxResponseBytes);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        JsonObject message;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            message = await ReadJsonFrameAsync(
                bounded,
                body["id"]?.GetValue<long>(),
                ct);
        }
        else
        {
            message = await JsonNode.ParseAsync(bounded, cancellationToken: ct) as JsonObject
                ?? throw new InvalidOperationException("MCP returned an invalid JSON-RPC response.");
        }
        return new HttpResult(message, nextSession);
    }

    private async Task CloseSessionAsync(
        HttpClient client,
        Uri uri,
        McpServer server,
        string sessionId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            ApplyHeaders(request, server);
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await client.SendAsync(request, timeout.Token);
        }
        catch
        {
            // Session cleanup is best-effort; the tool result must not be replaced by DELETE failure.
        }
    }

    private async Task PostLegacyAsync(
        HttpClient client,
        Uri endpoint,
        McpServer server,
        JsonObject body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, server);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP SSE message failed ({(int)response.StatusCode}).");
        }
    }

    private async Task<HttpClient> CreateClientAsync(Uri uri, CancellationToken ct)
    {
        var addresses = await networkGuard.ValidateAsync(uri, ct);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        last = exception;
                        socket.Dispose();
                        if (exception is OperationCanceledException) throw;
                    }
                }
                throw new HttpRequestException("Could not connect to the validated MCP address.", last);
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private void ApplyHeaders(HttpRequestMessage request, McpServer server)
    {
        foreach (var (key, encrypted) in SecretEntries(server.HeadersJson))
        {
            if (ForbiddenHeader(key)) continue;
            var value = McpSecretCodec.Unprotect(protector, encrypted);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static bool ForbiddenHeader(string key) => key.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<KeyValuePair<string, string>> SecretEntries(string json)
    {
        var entries = new List<KeyValuePair<string, string>>();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return entries;
            foreach (var (key, value) in obj)
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    entries.Add(KeyValuePair.Create(key, text));
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt optional headers are ignored; initialize reports the usable connection state.
        }
        return entries;
    }

    private static JsonObject InitializeParams() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "Sol", ["version"] = "1" },
    };

    private static JsonObject Request(long id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
    };

    private static JsonObject Notification(string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters,
    };

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void ThrowIfError(JsonObject response)
    {
        if (response["error"] is not JsonObject error) return;
        var message = error["message"]?.GetValue<string>() ?? "MCP JSON-RPC error.";
        throw new InvalidOperationException(message.Length <= 400 ? message : message[..400] + "…");
    }

    private static async Task<JsonObject> ReadJsonFrameAsync(
        Stream stream,
        long? expectedId,
        CancellationToken ct)
    {
        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data.Length == 0 || JsonNode.Parse(frame.Data) is not JsonObject result) continue;
            if (expectedId is null || result["id"]?.GetValue<long>() == expectedId) return result;
        }
        throw new InvalidOperationException("The MCP SSE response ended before its JSON-RPC response.");
    }

    private static async Task<Uri> ReadEndpointAsync(
        IAsyncEnumerator<SseFrame> frames,
        Uri streamUri,
        CancellationToken ct)
    {
        while (await frames.MoveNextAsync())
        {
            var frame = frames.Current;
            if (!string.Equals(frame.EventName, "endpoint", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(streamUri, frame.Data.Trim(), out var endpoint)) return endpoint;
            throw new InvalidOperationException("The MCP SSE endpoint event contained an invalid URL.");
        }
        throw new InvalidOperationException("The MCP SSE stream ended before its endpoint event.");
    }

    private static async Task<JsonObject> ReadResponseAsync(
        IAsyncEnumerator<SseFrame> frames,
        long id,
        CancellationToken ct)
    {
        while (await frames.MoveNextAsync())
        {
            var frame = frames.Current;
            if (frame.Data.Length == 0 || JsonNode.Parse(frame.Data) is not JsonObject message) continue;
            if (message["id"]?.GetValue<long>() == id) return message;
        }
        throw new InvalidOperationException($"The MCP SSE stream ended before response {id}.");
    }

    private static void EnsureSupported(McpServer server)
    {
        if (!server.Enabled) throw new InvalidOperationException("The MCP server is disabled.");
        if (server.Transport == McpTransport.Stdio)
        {
            throw new NotSupportedException("The stdio MCP transport is not supported.");
        }
    }

    private static Uri RequiredUri(McpServer server) =>
        Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException("The MCP server URL is invalid.");

    private sealed record HttpResult(JsonObject Message, string? SessionId);

    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Count(read);
            return read;
        }
        private void Count(int read)
        {
            _read += read;
            if (_read > limit) throw new InvalidOperationException("MCP response exceeded the size limit.");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
