using System.Text;
using Microsoft.Extensions.Options;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Models;
using Sol.Application.Abstractions.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Skills;

/// <summary>Runs approved Skill scripts in a short-lived OpenSandbox instance.</summary>
internal sealed class OpenSandboxSkillScriptRunner(
    IOptions<SkillsOptions> configuredOptions) : ISkillScriptRunner
{
    private readonly SkillsOptions options = configuredOptions.Value;

    public int MaximumTimeoutSeconds => options.RunnerTimeoutSeconds;
    public int MaximumPackageBytes => Math.Min(
        options.RunnerMaxPackageBytes,
        Math.Min(options.MaxUploadBytes, options.MaxExtractedBytes));
    public int MaximumFileCount => options.MaxEntries;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync($"http://{options.OpenSandboxDomain}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct)
    {
        Validate(request);
        var connection = new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = options.OpenSandboxDomain,
            ApiKey = options.OpenSandboxApiKey,
            RequestTimeoutSeconds = request.TimeoutSeconds(),
        });
        await using var sandbox = await Sandbox.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = connection,
            Image = options.OpenSandboxImage,
            TimeoutSeconds = request.TimeoutSeconds(),
            NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny },
        }, ct);

        try
        {
            await sandbox.Files.WriteFilesAsync(
                request.Files.Select(file => new WriteEntry
                {
                    Path = $"/workspace/{file.Path}",
                    Data = file.Bytes,
                    Mode = 384,
                }), ct);
            if (request.Stdin is not null)
            {
                await sandbox.Files.WriteFilesAsync(
                    [new WriteEntry { Path = "/workspace/.sol-stdin", Data = request.Stdin, Mode = 384 }],
                    ct);
            }

            var execution = await sandbox.Commands.RunAsync(
                BuildCommand(request),
                new RunCommandOptions
                {
                    WorkingDirectory = "/workspace",
                    TimeoutSeconds = request.TimeoutSeconds(),
                },
                handlers: null,
                ct);
            var stdout = Truncate(string.Concat(execution.Logs.Stdout.Select(message => message.Text)));
            var stderr = Truncate(string.Concat(execution.Logs.Stderr.Select(message => message.Text)));
            var succeeded = execution.ExitCode == 0;
            return new SkillScriptResult(
                succeeded ? "succeeded" : "failed",
                execution.ExitCode,
                stdout.Value,
                stderr.Value,
                stdout.Truncated,
                stderr.Truncated);
        }
        finally
        {
            await sandbox.KillAsync(ct);
        }
    }

    private string BuildCommand(SkillScriptRequest request)
    {
        var interpreter = Path.GetExtension(request.ScriptPath).ToLowerInvariant() switch
        {
            ".sh" => "sh",
            ".py" => "python3",
            ".js" => "node",
            _ => throw new InvalidDataException("Unsupported Skill script type."),
        };
        var arguments = string.Join(' ', request.Arguments.Select(ShellQuote));
        var command = $"{interpreter} {ShellQuote(request.ScriptPath)}";
        if (arguments.Length > 0) command += $" {arguments}";
        return request.Stdin is null ? command : $"{command} < .sol-stdin";
    }

    private void Validate(SkillScriptRequest request)
    {
        if (request.Files.Count == 0 || request.Files.Count > options.MaxEntries)
        {
            throw new InvalidDataException("The Skill execution package has an invalid file count.");
        }
        if (request.Files.Sum(file => (long)file.Bytes.Length) > MaximumPackageBytes)
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
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
        {
            throw new InvalidDataException("Skill script timeout exceeds the configured limit.");
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Invalid Skill path '{path}'.");
        }
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.Length > 255 || segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException($"Unsafe Skill path '{path}'.");
        }
        return path;
    }

    private (string Value, bool Truncated) Truncate(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= options.RunnerMaxOutputBytes) return (value, false);
        return (Encoding.UTF8.GetString(bytes, 0, options.RunnerMaxOutputBytes), true);
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\\"'\\\"'", StringComparison.Ordinal)}'";
}

internal static class SkillScriptRequestExtensions
{
    public static int TimeoutSeconds(this SkillScriptRequest request) =>
        checked((int)Math.Ceiling(request.Timeout.TotalSeconds));
}