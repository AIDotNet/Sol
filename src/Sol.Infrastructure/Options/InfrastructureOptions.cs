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
