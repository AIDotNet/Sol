namespace Sol.Infrastructure.Persistence.Migrations;

/// <param name="Checksum">SHA-256 of <paramref name="Sql"/>, compared on every startup.</param>
internal sealed record MigrationScript(int Version, string Name, string Sql, byte[] Checksum);
