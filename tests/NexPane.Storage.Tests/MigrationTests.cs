using Microsoft.Data.Sqlite;
using NexPane.Storage;

namespace NexPane.Storage.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexpane-mig-" + Guid.NewGuid().ToString("N"));

    public MigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnectionFactory.ClearPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    public static TheoryData<string> IndexObjects => new()
    {
        "table:volumes", "table:entries", "table:documents", "table:doc_text", "table:doc_text_fts", "table:transcript_segments",
        "table:ocr_lines", "table:embedding_models", "table:chunks", "table:embeddings", "table:work_queue",
        "index:ix_entries_parent", "index:ix_documents_path", "index:ix_work_queue_ready",
        "trigger:doc_text_ai", "trigger:doc_text_ad", "trigger:doc_text_au",
    };

    public static TheoryData<string> AppObjects => new()
    {
        "table:tags", "table:file_tags", "table:column_layouts", "table:saved_filters", "table:search_categories", "table:sessions",
        "table:session_tabs", "table:network_connections", "table:ssh_known_hosts", "table:tls_pins", "table:sync_pairs",
        "table:sync_baseline", "table:rename_batches", "table:rename_journal", "table:scripts", "table:ai_providers",
        "table:ai_feature_bindings", "table:ai_usage", "table:preview_blocklist",
        "index:ix_file_tags_key", "index:ux_sessions_autosave",
    };

    [Theory]
    [MemberData(nameof(IndexObjects))]
    public void Index_db_created_from_empty_contains(string typeAndName)
    {
        using var connection = CreateDatabase(DatabaseKind.Index, "index.db");

        SchemaObjects(connection).Should().Contain(typeAndName);
    }

    [Theory]
    [MemberData(nameof(AppObjects))]
    public void App_db_created_from_empty_contains(string typeAndName)
    {
        using var connection = CreateDatabase(DatabaseKind.App, "app.db");

        SchemaObjects(connection).Should().Contain(typeAndName);
    }

    [Theory]
    [InlineData(DatabaseKind.Index, "index.db")]
    [InlineData(DatabaseKind.App, "app.db")]
    public void Fresh_database_has_user_version_1_and_wal_pragmas(DatabaseKind kind, string file)
    {
        using var connection = CreateDatabase(kind, file);

        MigrationRunner.GetUserVersion(connection).Should().Be(1);
        Scalar(connection, "PRAGMA journal_mode;").Should().Be("wal");
        Scalar(connection, "PRAGMA foreign_keys;").Should().Be(1L);
        Scalar(connection, "PRAGMA synchronous;").Should().Be(1L); // NORMAL
        Scalar(connection, "PRAGMA busy_timeout;").Should().Be(5000L);
        Scalar(connection, "PRAGMA foreign_key_check;").Should().BeNull();
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        using var connection = factory.Open();

        MigrationRunner.Migrate(connection, DatabaseKind.Index).Should().Be(1);
        MigrationRunner.Migrate(connection, DatabaseKind.Index).Should().Be(0);
        MigrationRunner.GetUserVersion(connection).Should().Be(1);
    }

    [Fact]
    public void Fts_triggers_keep_doc_text_fts_consistent_through_insert_update_delete()
    {
        using var connection = CreateDatabase(DatabaseKind.Index, "index.db");
        Execute(connection, "INSERT INTO volumes(volume_id, kind, root) VALUES (1, 0, 'C:\\');");
        Execute(connection, """
            INSERT INTO documents(doc_id, volume_id, file_id, path, size, modified_utc, extractor, extract_status, indexed_utc)
            VALUES (1, 1, 100, 'C:\a.txt', 10, 1, 'text', 0, 1), (2, 1, 101, 'C:\b.txt', 10, 1, 'text', 0, 1);
            """);
        Execute(connection, "INSERT INTO doc_text(text_id, doc_id, kind, producer, text, created_utc) VALUES (1, 1, 1, 'text', 'invoice total amount', 1);");
        Execute(connection, "INSERT INTO doc_text(text_id, doc_id, kind, producer, text, created_utc) VALUES (2, 2, 1, 'text', 'café résumé', 1);");

        Match(connection, "\"invoice total\"").Should().Equal(1L);
        Match(connection, "cafe").Should().Equal(2L); // remove_diacritics 2 folds accents
        Match(connection, "inv*").Should().Equal(1L);

        Execute(connection, "UPDATE doc_text SET text = 'receipt sum' WHERE text_id = 1;");
        Match(connection, "invoice").Should().BeEmpty();
        Match(connection, "receipt").Should().Equal(1L);

        Execute(connection, "DELETE FROM documents WHERE doc_id = 2;"); // cascades to doc_text → delete trigger
        Match(connection, "cafe").Should().BeEmpty();
        Scalar(connection, "SELECT COUNT(*) FROM doc_text;").Should().Be(1L);

        var check = () => Execute(connection, "INSERT INTO doc_text_fts(doc_text_fts) VALUES ('integrity-check');");
        check.Should().NotThrow();
    }

    [Fact]
    public void Volume_delete_cascades_to_entries_documents_and_work_queue()
    {
        using var connection = CreateDatabase(DatabaseKind.Index, "index.db");
        Execute(connection, "INSERT INTO volumes(volume_id, kind, root) VALUES (7, 0, 'D:\\');");
        Execute(connection, "INSERT INTO entries(volume_id, file_id, parent_id, name, attributes) VALUES (7, 5, 5, 'D:', 16);");
        Execute(connection, "INSERT INTO documents(doc_id, volume_id, file_id, path, size, modified_utc, extractor, extract_status, indexed_utc) VALUES (9, 7, 5, 'D:\\x', 1, 1, 'text', 5, 1);");
        Execute(connection, "INSERT INTO work_queue(kind, doc_id, path, enqueued_utc) VALUES (1, 9, 'D:\\x', 1);");

        Execute(connection, "DELETE FROM volumes WHERE volume_id = 7;");

        Scalar(connection, "SELECT (SELECT COUNT(*) FROM entries) + (SELECT COUNT(*) FROM documents) + (SELECT COUNT(*) FROM work_queue);").Should().Be(0L);
    }

    [Fact]
    public void Autosave_session_is_unique()
    {
        using var connection = CreateDatabase(DatabaseKind.App, "app.db");
        Execute(connection, "INSERT INTO sessions(name, is_autosave, window_json, created_utc, updated_utc) VALUES ('a', 1, '{}', 1, 1);");
        Execute(connection, "INSERT INTO sessions(name, is_autosave, window_json, created_utc, updated_utc) VALUES ('b', 0, '{}', 1, 1);");

        var second = () => Execute(connection, "INSERT INTO sessions(name, is_autosave, window_json, created_utc, updated_utc) VALUES ('c', 1, '{}', 1, 1);");

        second.Should().Throw<SqliteException>();
    }

    [Fact]
    public void Database_newer_than_build_is_rejected()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "future.db"), DatabaseKind.Index);
        using var connection = factory.Open();
        Execute(connection, "PRAGMA user_version = 99;");

        var act = () => MigrationRunner.Migrate(connection, DatabaseKind.Index);

        act.Should().Throw<InvalidOperationException>().WithMessage("*newer*");
    }

    [Fact]
    public void Failed_index_db_is_deleted_and_rebuilt()
    {
        var path = Path.Combine(_dir, "index.db");
        File.WriteAllText(path, "this is not a sqlite database, the header is garbage................................................");
        var factory = new SqliteConnectionFactory(path, DatabaseKind.Index);

        var result = DatabaseBootstrapper.Open(factory);

        result.Rebuilt.Should().BeTrue();
        result.Version.Should().Be(1);
        using var connection = factory.Open();
        SchemaObjects(connection).Should().Contain("table:entries");
    }

    [Fact]
    public void Failed_app_db_is_backed_up_and_startup_aborts()
    {
        var path = Path.Combine(_dir, "app.db");
        var factory = new SqliteConnectionFactory(path, DatabaseKind.App);
        using (var connection = factory.Open())
        {
            Execute(connection, "PRAGMA user_version = 42;");
        }

        SqliteConnectionFactory.ClearPools();
        var act = () => DatabaseBootstrapper.Open(factory);

        var ex = act.Should().Throw<DatabaseStartupException>().Which;
        ex.BackupPath.Should().NotBeNull().And.Contain("app.db.bak-");
        File.Exists(ex.BackupPath!).Should().BeTrue();
        File.Exists(path).Should().BeTrue("app.db itself is never deleted");
    }

    [Fact]
    public void Bootstrap_of_a_healthy_database_reports_no_rebuild()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "app.db"), DatabaseKind.App);

        DatabaseBootstrapper.Open(factory).MigrationsApplied.Should().Be(1);
        var second = DatabaseBootstrapper.Open(factory);

        second.MigrationsApplied.Should().Be(0);
        second.Rebuilt.Should().BeFalse();
    }

    [Fact]
    public async Task Write_queue_serializes_writes_on_one_connection()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        DatabaseBootstrapper.Open(factory);
        await using var queue = new SqliteWriter(factory);

        var tasks = Enumerable.Range(0, 200).Select(i => queue.ExecuteInTransactionAsync((c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "INSERT INTO work_queue(kind, path, enqueued_utc) VALUES (1, $p, 1);";
            cmd.Parameters.AddWithValue("$p", "C:\\f" + i);
            return cmd.ExecuteNonQuery();
        })).ToList();
        await Task.WhenAll(tasks).WaitAsync(TestContext.Current.CancellationToken);

        using var reader = factory.Open();
        Scalar(reader, "SELECT COUNT(*) FROM work_queue;").Should().Be(200L);
    }

    [Fact]
    public async Task Write_queue_propagates_errors_and_keeps_working()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        DatabaseBootstrapper.Open(factory);
        await using var queue = new SqliteWriter(factory);

        var bad = () => queue.ExecuteAsync(c => Execute(c, "INSERT INTO no_such_table VALUES (1);"));
        await bad.Should().ThrowAsync<SqliteException>();

        var count = await queue.ExecuteAsync(c => Scalar(c, "SELECT COUNT(*) FROM volumes;"), TestContext.Current.CancellationToken);
        count.Should().Be(0L);
    }

    private SqliteConnection CreateDatabase(DatabaseKind kind, string file)
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, file), kind);
        var connection = factory.Open();
        MigrationRunner.Migrate(connection, kind);
        return connection;
    }

    private static List<string> SchemaObjects(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type || ':' || name FROM sqlite_master;";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static List<long> Match(SqliteConnection connection, string query)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM doc_text_fts WHERE doc_text_fts MATCH $q ORDER BY rowid;";
        cmd.Parameters.AddWithValue("$q", query);
        using var reader = cmd.ExecuteReader();
        var list = new List<long>();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    private static int Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
