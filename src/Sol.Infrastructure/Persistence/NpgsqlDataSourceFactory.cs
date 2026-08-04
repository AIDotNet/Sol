using Npgsql;

namespace Sol.Infrastructure.Persistence;

/// <summary>
/// Builds the application's <see cref="NpgsqlDataSource"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="NpgsqlSlimDataSourceBuilder"/>, which starts with optional type handlers
/// disabled and requires each one to be opted back in. That keeps the native binary small and,
/// more importantly, makes the AOT-hostile features impossible to enable by accident:
/// <c>EnableDynamicJson()</c> and <c>MapComposite&lt;T&gt;()</c> both need runtime code
/// generation and are annotated accordingly.
/// <para>
/// Consequence for callers: to store a POCO as jsonb, serialize it yourself with a
/// source-generated <c>JsonSerializerContext</c> and pass the resulting string.
/// </para>
/// </remarks>
public static class NpgsqlDataSourceFactory
{
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlSlimDataSourceBuilder(connectionString);

        // Required whenever the server offers TLS, which every managed PostgreSQL does.
        builder.EnableTransportSecurity();

        // inet columns (device.ip_prefix).
        builder.EnableNetworkTypes();

        // JsonDocument/JsonElement only — the AOT-safe subset, not POCO mapping.
        builder.EnableJsonTypes();

        // Array parameters, used by the migration runner and batch lookups.
        builder.EnableArrays();

        return builder.Build();
    }
}
