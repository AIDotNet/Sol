using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Skills;

internal sealed class UnixSocketSkillScriptRunner : ISkillScriptRunner, IDisposable
{
    private readonly SkillsOptions options;
    private readonly string socketPath;
    private readonly HttpClient client;

    public UnixSocketSkillScriptRunner(IOptions<SkillsOptions> configuredOptions)
    {
        options = configuredOptions.Value;
        socketPath = Path.GetFullPath(options.RunnerSocketPath);
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = ConnectAsync,
        };
        client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://skill-runner"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public int MaximumTimeoutSeconds => options.RunnerTimeoutSeconds;
    public int MaximumPackageBytes => Math.Min(
        options.RunnerMaxPackageBytes,
        Math.Min(options.MaxUploadBytes, options.MaxExtractedBytes));
    public int MaximumFileCount => options.MaxEntries;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (!Path.Exists(socketPath)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await client.GetAsync("/health", timeout.Token);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException or SocketException)
        {
            return false;
        }
    }

    public async Task<SkillScriptResult> RunAsync(
        SkillScriptRequest request,
        CancellationToken ct)
    {
        Validate(request);
        var files = new JsonArray();
        foreach (var file in request.Files)
        {
            files.Add((JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["contentBase64"] = Convert.ToBase64String(file.Bytes),
            });
        }
        var arguments = new JsonArray();
        foreach (var argument in request.Arguments)
        {
            arguments.Add((JsonNode)JsonValue.Create(argument));
        }
        var payload = new JsonObject
        {
            ["scriptPath"] = request.ScriptPath,
            ["arguments"] = arguments,
            ["stdin"] = request.Stdin,
            ["timeoutMs"] = (int)request.Timeout.TotalMilliseconds,
            ["maxOutputBytes"] = options.RunnerMaxOutputBytes,
            ["files"] = files,
        };

        using var body = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout + TimeSpan.FromSeconds(5));
        using var response = await client.PostAsync("/run", body, timeout.Token);
        var responseText = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token),
            checked(options.RunnerMaxOutputBytes * 2 + 64 * 1024),
            timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                responseText.Length == 0 ? "The Skill runner rejected the request." : responseText);
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            return new SkillScriptResult(
                ReadString(root, "status") ?? "failed",
                root.TryGetProperty("exitCode", out var exitCode)
                    && exitCode.ValueKind == JsonValueKind.Number
                    && exitCode.TryGetInt32(out var parsedExitCode)
                        ? parsedExitCode
                        : null,
                ReadString(root, "stdout") ?? string.Empty,
                ReadString(root, "stderr") ?? string.Empty,
                ReadBool(root, "stdoutTruncated"),
                ReadBool(root, "stderrTruncated"));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Skill runner returned invalid JSON.", exception);
        }
    }

    public void Dispose() => client.Dispose();

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void Validate(SkillScriptRequest request)
    {
        if (request.Files.Count == 0 || request.Files.Count > options.MaxEntries)
        {
            throw new InvalidDataException("The Skill execution package has an invalid file count.");
        }
        var packageBytes = request.Files.Sum(file => (long)file.Bytes.Length);
        if (packageBytes > options.MaxExtractedBytes)
        {
            throw new InvalidDataException("The Skill execution package exceeds its size limit.");
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in request.Files)
        {
            if (!paths.Add(ValidatePath(file.Path)))
            {
                throw new InvalidDataException("The Skill execution package contains duplicate paths.");
            }
        }
        var scriptPath = ValidatePath(request.ScriptPath);
        if (!paths.Contains(scriptPath)) throw new FileNotFoundException("Skill script not found.");
        if (Path.GetExtension(scriptPath).ToLowerInvariant() is not (".sh" or ".py" or ".js"))
        {
            throw new InvalidDataException("Only .sh, .py, and .js Skill scripts can run.");
        }
        if (request.Arguments.Count > 64 || request.Arguments.Any(argument => argument.Length > 4096))
        {
            throw new InvalidDataException("Skill script arguments exceed their limit.");
        }
        if (request.Stdin is { } stdin && Encoding.UTF8.GetByteCount(stdin) > 256 * 1024)
        {
            throw new InvalidDataException("Skill script stdin exceeds 256 KiB.");
        }
        if (request.Timeout <= TimeSpan.Zero
            || request.Timeout > TimeSpan.FromSeconds(options.RunnerTimeoutSeconds))
        {
            throw new InvalidDataException("Skill script timeout exceeds the configured limit.");
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\')
            || path.StartsWith('/') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Invalid Skill path '{path}'.");
        }
        var segments = path.Split('/');
        if (segments.Length == 0 || segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Length > 255
                || segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException($"Unsafe Skill path '{path}'.");
        }
        var normalized = string.Join('/', segments);
        if (normalized.Length > 512 || Encoding.UTF8.GetByteCount(normalized) > 1_024)
        {
            throw new InvalidDataException("A Skill path exceeds its length limit.");
        }
        return normalized;
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("The Skill runner response exceeds its size limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(output.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Skill runner response is not valid UTF-8.", exception);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
