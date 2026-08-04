using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Sol.Infrastructure.Persistence.Migrations;

/// <summary>
/// Applies embedded SQL migrations in order.
/// </summary>
/// <remarks>
/// EF Core Migrations rely on runtime model building and are unavailable under Native AOT, so
/// schema changes are plain versioned SQL embedded into the assembly — a single native binary
/// carries the schema it expects.
/// </remarks>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    /// <summary>
    /// Arbitrary but fixed key identifying this application's schema lock.
    /// </summary>
    private const long AdvisoryLockKey = 0x501_5C4E_3A21L;

    private const string ResourcePrefix = "Sol.Migrations.";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var scripts = LoadEmbeddedScripts();
        if (scripts.Count == 0)
        {
            logger.LogWarning("No embedded migration scripts found.");
            return 0;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // Serialises concurrent instances. Without it, replicas starting together race on DDL
        // and deadlock. The lock is session-scoped and released explicitly below.
        await connection.ExecuteAsync("SELECT pg_advisory_lock(@Key)", new LockArgs { Key = AdvisoryLockKey });

        try
        {
            await EnsureVersionTableAsync(connection, scripts[0], ct);

            var applied = (await connection.QueryAsync<AppliedMigration>(
                    "SELECT version, name, checksum FROM schema_version ORDER BY version"))
                .ToDictionary(m => m.Version);

            VerifyChecksums(scripts, applied);

            var pending = scripts.Where(s => !applied.ContainsKey(s.Version)).ToList();
            foreach (var script in pending)
            {
                await ApplyAsync(connection, script, ct);
                logger.LogInformation("Applied migration {Version} ({Name}).", script.Version, script.Name);
            }

            return pending.Count;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new LockArgs { Key = AdvisoryLockKey });
        }
    }

    /// <summary>
    /// Runs migration 0001 (which creates schema_version itself) outside the bookkeeping path,
    /// then records it. Every script is written to be idempotent, so a re-run is harmless.
    /// </summary>
    private static async Task EnsureVersionTableAsync(
        NpgsqlConnection connection, MigrationScript first, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = first.Sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Recomputes the hash of every already-applied script and stops the application if one has
    /// changed. A shipped migration that is edited in place leaves environments silently
    /// divergent, which is far more expensive to unpick than a failed startup.
    /// </summary>
    private void VerifyChecksums(
        IReadOnlyList<MigrationScript> scripts, IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        foreach (var script in scripts)
        {
            if (!applied.TryGetValue(script.Version, out var record))
            {
                continue;
            }

            if (!record.Checksum.AsSpan().SequenceEqual(script.Checksum))
            {
                throw new InvalidOperationException(
                    $"Migration {script.Version} ({script.Name}) has been modified after it was applied. " +
                    "Revert the edit and add a new migration instead.");
            }
        }

        logger.LogDebug("Verified {Count} applied migration checksums.", applied.Count);
    }

    /// <summary>
    /// Applies one script and records it in the same transaction, so a partially-applied
    /// migration can never be marked as complete.
    /// </summary>
    private static async Task ApplyAsync(
        NpgsqlConnection connection, MigrationScript script, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = script.Sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO schema_version (version, name, checksum)
            VALUES (@Version, @Name, @Checksum)
            ON CONFLICT (version) DO NOTHING
            """,
            new InsertMigrationArgs
            {
                Version = script.Version,
                Name = script.Name,
                Checksum = script.Checksum,
            },
            transaction);

        await transaction.CommitAsync(ct);
    }

    private static List<MigrationScript> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resourceName[ResourcePrefix.Length..];
            var separator = fileName.IndexOf("__", StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName[..separator], out var version))
            {
                throw new InvalidOperationException(
                    $"Migration resource '{resourceName}' does not follow the NNNN__name.sql convention.");
            }

            var sql = ReadResource(assembly, resourceName);
            scripts.Add(new MigrationScript(
                version,
                fileName[(separator + 2)..].Replace(".sql", string.Empty, StringComparison.Ordinal),
                sql,
                SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        return scripts;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

// Dapper.AOT requires statically-typed parameter objects: object, dynamic, DynamicParameters
// and generic T all switch the generator off (DAP015/DAP016) and fall back to reflection.
internal sealed class LockArgs
{
    public long Key { get; init; }
}

internal sealed class InsertMigrationArgs
{
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte[] Checksum { get; init; } = [];
}

internal sealed class AppliedMigration
{
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte[] Checksum { get; init; } = [];
}
