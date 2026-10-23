using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NexPane.Storage;

/// <summary>Outcome of opening and migrating a database at startup.</summary>
public sealed record DatabaseOpenResult(DatabaseKind Kind, string Path, int Version, int MigrationsApplied, bool Rebuilt, string? BackupPath);

/// <summary>
/// Startup policy of §9: migrations run at startup; a failed migration on <c>index.db</c> deletes and rebuilds the
/// database; on <c>app.db</c> it copies the file to <c>app.db.bak-&lt;timestamp&gt;</c> and aborts startup.
/// </summary>
public static class DatabaseBootstrapper
{
    public static DatabaseOpenResult Open(SqliteConnectionFactory factory, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        clock ??= TimeProvider.System;
        try
        {
            using var connection = factory.Open();
            var applied = MigrationRunner.Migrate(connection, factory.Kind);
            return new DatabaseOpenResult(factory.Kind, factory.DatabasePath, MigrationRunner.GetUserVersion(connection), applied, false, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            SqliteConnectionFactory.ClearPools();
            if (factory.Kind == DatabaseKind.Index)
            {
                DeleteDatabaseFiles(factory.DatabasePath);
                using var connection = factory.Open();
                var applied = MigrationRunner.Migrate(connection, factory.Kind);
                return new DatabaseOpenResult(factory.Kind, factory.DatabasePath, MigrationRunner.GetUserVersion(connection), applied, true, null);
            }

            var backup = factory.DatabasePath + ".bak-" + clock.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            if (File.Exists(factory.DatabasePath))
            {
                File.Copy(factory.DatabasePath, backup, overwrite: true);
            }

            throw new DatabaseStartupException(
                $"app.db could not be migrated ({ex.Message}). A backup copy was written to '{backup}'.", backup, ex);
        }
    }

    /// <summary>Deletes a database file together with its <c>-wal</c> and <c>-shm</c> side files.</summary>
    public static void DeleteDatabaseFiles(string databasePath)
    {
        SqliteConnectionFactory.ClearPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var file = databasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>Thrown when <c>app.db</c> cannot be opened or migrated; startup must abort.</summary>
public sealed class DatabaseStartupException : Exception
{
    public DatabaseStartupException()
    {
    }

    public DatabaseStartupException(string message)
        : base(message)
    {
    }

    public DatabaseStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DatabaseStartupException(string message, string? backupPath, Exception innerException)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }

    public string? BackupPath { get; }
}
