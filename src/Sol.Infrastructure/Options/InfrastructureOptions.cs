namespace Sol.Infrastructure.Options;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether to apply pending migrations during startup. Convenient in development; in
    /// production prefer running the same binary with <c>--migrate-only</c> as a deploy step,
    /// so schema changes are an explicit action rather than a side effect of a rollout.
    /// </summary>
    public bool RunMigrationsOnStartup { get; set; }
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string Configuration { get; set; } = string.Empty;

    public string InstanceName { get; set; } = "sol:";
}

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "sol.events";
    public string QueueName { get; set; } = "sol.events.api";
    public ushort PrefetchCount { get; set; } = 16;

    /// <summary>Routing keys the consumer binds. Empty means bind nothing and stay idle.</summary>
    public string[] RoutingKeys { get; set; } = ["device.#"];
}

public sealed class SkillsOptions
{
    public const string SectionName = "Skills";

    /// <summary>
    /// Enables execution of Skill scripts through the isolated runner. When disabled, Skill
    /// instructions and resources remain readable but scripts are never offered or executed.
    /// </summary>
    public bool SandboxEnabled { get; set; }

    public string Root { get; set; } = "./storage/skills";
    public int MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxExtractedBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxEntries { get; set; } = 1_000;
    public string OpenSandboxDomain { get; set; } = "localhost:8090";
    public string OpenSandboxApiKey { get; set; } = string.Empty;
    public string OpenSandboxImage { get; set; } = "opensandbox/code-interpreter:v1.1.0";
    public int RunnerTimeoutSeconds { get; set; } = 30;
    public int RunnerMaxOutputBytes { get; set; } = 256 * 1024;
    public int RunnerMaxPackageBytes { get; set; } = 25 * 1024 * 1024;
}

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>Allows plain HTTP only when every resolved address is loopback.</summary>
    public bool AllowLoopbackHttp { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxResponseBytes { get; set; } = 1_048_576;
}

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// Base64-encoded 32-byte key used to encrypt stored provider API keys.
    /// </summary>
    /// <remarks>
    /// Rotating this makes every stored key undecryptable — users would have to re-enter each
    /// one. Unlike <c>DeviceIdentity:Pepper</c>, whose rotation only drops probabilistic links,
    /// rotating this destroys data. Treat it as durable secret material.
    /// </remarks>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>
    /// The origin reference images are published under, e.g. <c>https://sol.example.com</c>.
    /// </summary>
    /// <remarks>
    /// Some upstreams accept reference images only as http(s) URLs, so the API hands out
    /// <c>{PublicOrigin}/api/v1/canvas/assets/{id}?token=…</c> links signed with the encryption
    /// key. Falls back to <c>Authentication:PublicOrigin</c> — the site origin proxies /api —
    /// and when neither is set, protocols that need a URL inline the image bytes instead.
    /// </remarks>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>
    /// When true, provider base URLs (and download links returned by an upstream) may resolve to
    /// loopback or private addresses — needed for a locally hosted model such as Ollama.
    /// </summary>
    /// <remarks>
    /// Off by default: with it off, the shared upstream HTTP client refuses to connect to any
    /// loopback/private/reserved address at connect time, which is what keeps a user-supplied
    /// base URL from becoming a probe of the server's own network. Enable only when every user
    /// of the deployment is trusted (single-user self-hosting); a public instance must keep it
    /// off, or anyone could make the server fetch internal endpoints.
    /// </remarks>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Where generated images and video are written. Only paths are stored in the database.</summary>
    public string AssetRoot { get; set; } = "./storage/assets";

    /// <summary>
    /// The shortest upstream timeout the application will honour, whatever configuration asks for.
    /// </summary>
    /// <remarks>
    /// Image and video models routinely render for several minutes, and a request cut short is
    /// not a retry — the vendor keeps rendering, the user is still billed, and the result is
    /// discarded. Ten minutes is the floor below which that becomes common rather than rare.
    /// </remarks>
    public const int MinimumRequestTimeoutSeconds = 600;

    /// <summary>
    /// How long to wait on an upstream generation call. Values below
    /// <see cref="MinimumRequestTimeoutSeconds"/> are raised to it when bound.
    /// </summary>
    /// <remarks>
    /// Keep this in step with <c>experimental.proxyTimeout</c> in <c>web/next.config.ts</c>.
    /// Whichever of the two is shorter is the one that actually ends the request, and when it is
    /// the proxy the failure arrives as a client disconnect rather than a timeout, which reads
    /// like a different bug entirely.
    /// </remarks>
    public int RequestTimeoutSeconds { get; set; } = 1200;
}
