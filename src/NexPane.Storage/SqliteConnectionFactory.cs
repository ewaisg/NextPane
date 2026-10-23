using Microsoft.Data.Sqlite;

namespace NexPane.Storage;

/// <summary>Which of the two NexPane databases (ARCHITECTURE §9).</summary>
public enum DatabaseKind
{
    /// <summary><c>index.db</c>: disposable, rebuildable.</summary>
    Index,

    /// <summary><c>app.db</c>: durable user data.</summary>
    App,
}

/// <summary>
/// Opens SQLite connections with the NexPane pragmas (§9): WAL, <c>synchronous=NORMAL</c>, <c>foreign_keys=ON</c>,
/// <c>busy_timeout=5000</c>. Reader connections are pooled by Microsoft.Data.Sqlite; writers go through
/// <see cref="SqliteWriter"/> (single-writer discipline).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath, DatabaseKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Kind = kind;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public DatabaseKind Kind { get; }

    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            ApplyPragmas(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            ApplyPragmas(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Releases pooled connections so the database file can be deleted or moved.</summary>
    public static void ClearPools() => SqliteConnection.ClearAllPools();

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }
}
