using Microsoft.Extensions.Configuration;
using Sol.Application.Features.Agent;
using Sol.Application.Features.Identity;

namespace Sol.Infrastructure.Options;

/// <summary>
/// Binds configuration sections by hand.
/// </summary>
/// <remarks>
/// <c>services.Configure&lt;T&gt;(IConfiguration)</c> is annotated
/// <c>RequiresUnreferencedCode</c>/<c>RequiresDynamicCode</c> because it reflects over the
/// options type. Under Native AOT that binding can quietly produce an object with default
/// values — the app then starts with an empty connection string and fails somewhere far from
/// the cause. Reading each key explicitly costs a few lines and removes the failure mode.
/// </remarks>
internal static class OptionsBinder
{
    public static PostgresOptions BindPostgres(IConfiguration configuration)
    {
        var section = configuration.GetSection(PostgresOptions.SectionName);
        return new PostgresOptions
        {
            ConnectionString = section["ConnectionString"] ?? string.Empty,
            RunMigrationsOnStartup = ReadBool(section["RunMigrationsOnStartup"], false),
        };
    }

    public static RedisOptions BindRedis(IConfiguration configuration)
    {
        var section = configuration.GetSection(RedisOptions.SectionName);
        return new RedisOptions
        {
            Configuration = section["Configuration"] ?? "localhost:6379",
            InstanceName = section["InstanceName"] ?? "sol:",
        };
    }

    public static RabbitMqOptions BindRabbitMq(IConfiguration configuration)
    {
        var section = configuration.GetSection(RabbitMqOptions.SectionName);
        var routingKeys = section.GetSection("RoutingKeys").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();

        return new RabbitMqOptions
        {
            HostName = section["HostName"] ?? "localhost",
            Port = ReadInt(section["Port"], 5672),
            UserName = section["UserName"] ?? "guest",
            Password = section["Password"] ?? "guest",
            VirtualHost = section["VirtualHost"] ?? "/",
            Exchange = section["Exchange"] ?? "sol.events",
            QueueName = section["QueueName"] ?? "sol.events.api",
            PrefetchCount = (ushort)ReadInt(section["PrefetchCount"], 16),
            RoutingKeys = routingKeys.Length > 0 ? routingKeys : ["device.#"],
        };
    }

    public static DeviceIdentityOptions BindDeviceIdentity(IConfiguration configuration)
    {
        var section = configuration.GetSection(DeviceIdentityOptions.SectionName);
        return new DeviceIdentityOptions
        {
            Pepper = section["Pepper"] ?? string.Empty,
            SignalVersion = ReadInt(section["SignalVersion"], 1),
            CoarseWindowHours = ReadInt(section["CoarseWindowHours"], 24),
            MaxCoarseFanout = ReadInt(section["MaxCoarseFanout"], 5),
            CookieName = section["CookieName"] ?? "sol_did",
            CookieMaxAgeDays = ReadInt(section["CookieMaxAgeDays"], 730),
        };
    }

    public static AuthenticationOptions BindAuthentication(IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthenticationOptions.SectionName);
        var github = section.GetSection("GitHub");

        return new AuthenticationOptions
        {
            PublicOrigin = section["PublicOrigin"] ?? "http://localhost:3000",
            SessionCookieName = section["SessionCookieName"] ?? "sol_session",
            SessionMaxAgeDays = Math.Clamp(ReadInt(section["SessionMaxAgeDays"], 30), 1, 365),
            OAuthTransactionMinutes = Math.Clamp(
                ReadInt(section["OAuthTransactionMinutes"], 10), 1, 30),
            GitHub = new GitHubAuthenticationOptions
            {
                Enabled = ReadBool(github["Enabled"], false),
                ClientId = github["ClientId"] ?? string.Empty,
                ClientSecret = github["ClientSecret"] ?? string.Empty,
                Scope = github["Scope"] ?? "read:user user:email",
            },
        };
    }

    public static SkillsOptions BindSkills(IConfiguration configuration)
    {
        var section = configuration.GetSection(SkillsOptions.SectionName);
        return new SkillsOptions
        {
            SandboxEnabled = ReadBool(section["SandboxEnabled"], false),
            Root = section["Root"] ?? "./storage/skills",
            MaxUploadBytes = Math.Clamp(
                ReadInt(section["MaxUploadBytes"], 25 * 1024 * 1024), 1_048_576, 52_428_800),
            MaxExtractedBytes = Math.Clamp(
                ReadInt(section["MaxExtractedBytes"], 100 * 1024 * 1024), 1_048_576, 209_715_200),
            MaxEntries = Math.Clamp(ReadInt(section["MaxEntries"], 1_000), 10, 5_000),
            OpenSandboxDomain = section["OpenSandboxDomain"] ?? "localhost:8090",
            OpenSandboxApiKey = section["OpenSandboxApiKey"] ?? string.Empty,
            OpenSandboxImage = section["OpenSandboxImage"]
                ?? "opensandbox/code-interpreter:v1.1.0",
            RunnerTimeoutSeconds = Math.Clamp(ReadInt(section["RunnerTimeoutSeconds"], 30), 1, 120),
            RunnerMaxOutputBytes = Math.Clamp(
                ReadInt(section["RunnerMaxOutputBytes"], 256 * 1024), 16_384, 1_048_576),
            // 25 MiB expands to ~33.4 MiB as base64, leaving bounded room for paths, stdin,
            // arguments, and JSON under the runner's 40 MiB request envelope.
            RunnerMaxPackageBytes = Math.Clamp(
                ReadInt(section["RunnerMaxPackageBytes"], 25 * 1024 * 1024),
                1_048_576, 25 * 1024 * 1024),
        };
    }

    public static McpOptions BindMcp(IConfiguration configuration)
    {
        var section = configuration.GetSection(McpOptions.SectionName);
        return new McpOptions
        {
            AllowLoopbackHttp = ReadBool(section["AllowLoopbackHttp"], false),
            RequestTimeoutSeconds = Math.Clamp(ReadInt(section["RequestTimeoutSeconds"], 30), 5, 120),
            MaxResponseBytes = Math.Clamp(
                ReadInt(section["MaxResponseBytes"], 1_048_576), 65_536, 4_194_304),
        };
    }

    public static AiOptions BindAi(IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        var publicOrigin = section["PublicOrigin"];

        // Default to the site origin: the deployments that need public asset links proxy /api
        // behind it, and they all already configure it for OAuth — one less key to set.
        if (string.IsNullOrWhiteSpace(publicOrigin))
        {
            publicOrigin = configuration["Authentication:PublicOrigin"];
        }

        return new AiOptions
        {
            EncryptionKey = section["EncryptionKey"] ?? string.Empty,
            PublicOrigin = publicOrigin?.TrimEnd('/') ?? string.Empty,
            AllowPrivateNetworks = ReadBool(section["AllowPrivateNetworks"], false),
            AssetRoot = section["AssetRoot"] ?? "./storage/assets",
            RequestTimeoutSeconds = Math.Max(
                AiOptions.MinimumRequestTimeoutSeconds,
                ReadInt(section["RequestTimeoutSeconds"], 1200)),
        };
    }

    public static AgentOptions BindAgent(IConfiguration configuration)
    {
        var section = configuration.GetSection(AgentOptions.SectionName);
        return new AgentOptions
        {
            // Clamped: a low floor keeps a misconfigured zero from turning every run into an
            // instant limit summary, and the ceiling bounds how long one run can burn tokens.
            MaxToolIterations = Math.Clamp(ReadInt(section["MaxToolIterations"], 40), 4, 200),
        };
    }

    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;
}
