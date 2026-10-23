using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NexPane.Storage;

/// <summary>One embedded migration script <c>NNNN_description.sql</c>.</summary>
public sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Applies the ordered embedded <c>.sql</c> migrations of a database whose number is greater than
/// <c>PRAGMA user_version</c>, each inside a transaction, then sets <c>user_version</c> (§9).
/// </summary>
public static partial class MigrationRunner
{
    public static IReadOnlyList<Migration> GetMigrations(DatabaseKind kind)
    {
        var prefix = kind switch
        {
            DatabaseKind.Index => "NexPane.Storage.Migrations.Index.",
            DatabaseKind.App => "NexPane.Storage.Migrations.App.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown database kind."),
        };

        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[prefix.Length..];
            var match = MigrationFileName().Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Embedded migration '{fileName}' does not follow the NNNN_description.sql pattern.");
            }

            migrations.Add(new Migration(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                match.Groups[2].Value,
                ReadResource(assembly, resource)));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migrations for {kind} must be numbered 1..N without gaps; found {migrations[i].Version} at position {i + 1}.");
            }
        }

        return migrations;
    }

    /// <summary>Latest schema version shipped for <paramref name="kind"/>.</summary>
    public static int LatestVersion(DatabaseKind kind) => GetMigrations(kind).Count;

    public static int GetUserVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Applies pending migrations. Returns the number applied.</summary>
    /// <exception cref="InvalidOperationException">The database is newer than this build.</exception>
    public static int Migrate(SqliteConnection connection, DatabaseKind kind)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var migrations = GetMigrations(kind);
        var current = GetUserVersion(connection);
        if (current > migrations.Count)
        {
            throw new InvalidOperationException(
                $"{kind} database schema version {current} is newer than this NexPane build supports ({migrations.Count}).");
        }

        var applied = 0;
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};");
                version.ExecuteNonQuery();
            }

            transaction.Commit();
            applied++;
        }

        return applied;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^(\d{4})_([A-Za-z0-9_\-]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileName();
}
