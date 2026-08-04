using Microsoft.Extensions.Configuration;
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

    public static AiOptions BindAi(IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        return new AiOptions
        {
            EncryptionKey = section["EncryptionKey"] ?? string.Empty,
            AssetRoot = section["AssetRoot"] ?? "./storage/assets",
            RequestTimeoutSeconds = Math.Max(
                AiOptions.MinimumRequestTimeoutSeconds,
                ReadInt(section["RequestTimeoutSeconds"], 1200)),
        };
    }

    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;
}
