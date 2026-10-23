using BenchmarkDotNet.Attributes;
using NexPane.Core.Indexing;
using NexPane.Indexing.Content;
using NexPane.Indexing.Store;
using NexPane.Storage;

namespace NexPane.Benchmarks;

/// <summary>P3.08: snapshot load of 1M entries from index.db (budget ≤ 2 s).</summary>
[SimpleJob(warmupCount: 1, iterationCount: 5, invocationCount: 1)]
public class SnapshotLoadBenchmarks
{
    private string _dir = null!;
    private SqliteConnectionFactory _factory = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexpane-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        DatabaseBootstrapper.Open(_factory);
        using var writer = new SqliteWriter(_factory);
        var store = new IndexStore();
        var handle = store.RegisterVolumeAsync(new IndexVolumeInfo(@"D:\", VolumeKind.NtfsFixed), 5, CancellationToken.None).GetAwaiter().GetResult();
        var random = new Random(42);
        var directories = new List<ulong> { 5 };
        var batch = new List<IndexDelta>(8192);
        for (var i = 0; i < 1_000_000; i++)
        {
            var id = 1000UL + (ulong)i;
            var parent = directories[random.Next(Math.Max(0, directories.Count - 64), directories.Count)];
            if (i % 10 == 0)
            {
                directories.Add(id);
                batch.Add(IndexDelta.UpsertById(id, parent, $"folder_{i}", 0x10, 0, 1, 1));
            }
            else
            {
                batch.Add(IndexDelta.UpsertById(id, parent, $"report_{i:D7}.pdf", 0x20, i, 1, 1));
            }

            if (batch.Count == 8192)
            {
                store.SubmitAsync(handle.Id, batch, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                batch = new List<IndexDelta>(8192);
            }
        }

        store.SubmitAsync(handle.Id, batch, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        store.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        var persistence = new IndexSnapshotPersistence(store, _factory, writer);
        persistence.FlushAsync(full: true, CancellationToken.None).GetAwaiter().GetResult();
        persistence.DisposeAsync().AsTask().GetAwaiter().GetResult();
        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public int Load1M()
    {
        using var writer = new SqliteWriter(_factory);
        var store = new IndexStore();
        var persistence = new IndexSnapshotPersistence(store, _factory, writer);
        var loaded = persistence.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        var count = store.CurrentGeneration.Count(loaded[0].Handle.Id);
        persistence.DisposeAsync().AsTask().GetAwaiter().GetResult();
        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        SqliteConnectionFactory.ClearPools();
        Directory.Delete(_dir, recursive: true);
    }
}

/// <summary>P3.32: FTS5 <c>content:</c> query over 100k documents (budget p95 ≤ 50 ms).</summary>
[SimpleJob(warmupCount: 2, iterationCount: 20)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet owns the lifecycle: [GlobalCleanup] disposes.")]
public class ContentSearchBenchmarks
{
    private string _dir = null!;
    private SqliteConnectionFactory _factory = null!;
    private SqliteWriter _writer = null!;
    private ContentStore _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexpane-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        DatabaseBootstrapper.Open(_factory);
        _writer = new SqliteWriter(_factory);
        _store = new ContentStore(_factory, _writer);
        var random = new Random(9);
        var vocabulary = Enumerable.Range(0, 30_000).Select(i => "w" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        _writer.ExecuteInTransactionAsync((c, t) =>
        {
            using var volume = c.CreateCommand();
            volume.Transaction = t;
            volume.CommandText = "INSERT INTO volumes(volume_id, kind, root) VALUES (1, 0, 'X:\\');";
            volume.ExecuteNonQuery();
            using var doc = c.CreateCommand();
            doc.Transaction = t;
            doc.CommandText = "INSERT INTO documents(doc_id, volume_id, file_id, path, size, modified_utc, extractor, extract_status, indexed_utc) VALUES ($id, 1, $id, $p, 1, 1, 'text', 0, 1);";
            var pId = doc.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pPath = doc.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
            using var text = c.CreateCommand();
            text.Transaction = t;
            text.CommandText = "INSERT INTO doc_text(doc_id, kind, producer, text, created_utc) VALUES ($id, 1, 'text', $t, 1);";
            var tId = text.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var tText = text.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Text);
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i <= 100_000; i++)
            {
                sb.Clear();
                for (var w = 0; w < 60; w++)
                {
                    sb.Append(vocabulary[(int)(Math.Pow(random.NextDouble(), 3) * vocabulary.Length)]).Append(' ');
                }

                if (i % 997 == 0)
                {
                    sb.Append("the invoice total is due");
                }

                pId.Value = i;
                pPath.Value = $"X:\\docs\\d{i}.txt";
                doc.ExecuteNonQuery();
                tId.Value = i;
                tText.Value = sb.ToString();
                text.ExecuteNonQuery();
            }

            return 0;
        }).GetAwaiter().GetResult();
    }

    [Params("\"invoice total\"", "\"w1f4\"", "\"w75\"*")]
    public string Query { get; set; } = string.Empty;

    [Benchmark]
    public int FullText100k()
    {
        var count = 0;
        var e = _store.FullTextAsync(Query, 200, CancellationToken.None).GetAsyncEnumerator();
        while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            count++;
        }

        e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        SqliteConnectionFactory.ClearPools();
        Directory.Delete(_dir, recursive: true);
    }
}
