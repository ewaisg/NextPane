using System.Diagnostics;
using System.Text;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Indexing.Content;
using NexPane.Indexing.Sources;
using NexPane.Indexing.Store;
using NexPane.Search.Execution;
using NexPane.Search.Language;
using NexPane.Shell.Content;
using NexPane.Storage;

namespace NexPane.Indexing.Tests;

public sealed class ContentPipelineTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexpane-content-" + Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _factory = null!;
    private SqliteWriter _writer = null!;
    private IFilterTextReader _ifilter = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        DatabaseBootstrapper.Open(_factory);
        _writer = new SqliteWriter(_factory);
        _ifilter = new IFilterTextReader();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _ifilter.Dispose();
        await _writer.DisposeAsync();
        SqliteConnectionFactory.ClearPools();
        Directory.Delete(_dir, recursive: true);
    }

    private IReadOnlyList<IContentExtractor> Extractors =>
        [new PlainTextExtractor(), new PdfPigExtractor(), new OpenXmlExtractor(), new EmlExtractor(), new MsgExtractor(), new IFilterExtractor(_ifilter)];

    [Theory]
    [InlineData("utf8.txt", "invoice total is 42 €", "text")]
    [InlineData("utf8-bom.txt", "Grüße aus Köln", "text")]
    [InlineData("utf16.txt", "naïve résumé", "text")]
    [InlineData("cp1252.txt", "Smørrebrød costs 5 €", "text")]
    [InlineData("invoice.pdf", "invoice total is 1,234.56", "pdfpig")]
    [InlineData("letter.docx", "invoice total attached", "openxml")]
    [InlineData("budget.xlsx", "Travel budget", "openxml")]
    [InlineData("deck.pptx", "Revenue grew by twelve percent", "openxml")]
    [InlineData("mail.eml", "invoice total for October", "mimekit")]
    [InlineData("mail.msg", "invoice total review", "msgreader")]
    [InlineData("doc.rtf", "Rich text says hello", "ifilter")]
    public async Task Extracts_expected_text_from_each_fixture(string file, string snippet, string extractorName)
    {
        var path = ContentFixtures.PathOf(file);
        var extension = Path.GetExtension(path).TrimStart('.');
        var extractor = Extractors.First(e => e.CanExtract(extension));

        var result = await extractor.ExtractAsync(path, 1_000_000, Ct);

        extractor.Name.Should().Be(extractorName);
        result.Status.Should().Be(ExtractStatus.Ok);
        result.Text.Should().Contain(snippet);
    }

    [Fact]
    public async Task Spreadsheet_and_mail_headers_are_included()
    {
        (await new OpenXmlExtractor().ExtractAsync(ContentFixtures.PathOf("budget.xlsx"), 10_000, Ct)).Text.Should().Contain("2400").And.Contain("Office supplies");
        (await new EmlExtractor().ExtractAsync(ContentFixtures.PathOf("mail.eml"), 10_000, Ct)).Text.Should().Contain("Your monthly statement");
        (await new MsgExtractor().ExtractAsync(ContentFixtures.PathOf("mail.msg"), 10_000, Ct)).Text.Should().Contain("Project kickoff").And.Contain("Team Lead");
    }

    [Fact]
    public void Binary_content_is_rejected_by_the_text_sniffer()
    {
        PlainTextExtractor.Decode(File.ReadAllBytes(ContentFixtures.PathOf("binary.dat"))).Should().BeNull();
        PlainTextExtractor.Decode(Encoding.UTF8.GetBytes("plain")).Should().Be("plain");
    }

    [Fact]
    public async Task Text_is_truncated_at_the_limit()
    {
        var result = await new PlainTextExtractor().ExtractAsync(ContentFixtures.PathOf("utf8.txt"), 10, Ct);

        result.Text.Should().HaveLength(10);
    }

    [Fact]
    public async Task Scheduler_extracts_queued_files_into_documents_and_fts()
    {
        var store = new ContentStore(_factory, _writer);
        var files = CopyFixtures("invoice.pdf", "other.pdf", "letter.docx", "mail.msg", "binary.dat");
        await using var scheduler = new ContentScheduler(store, Extractors);
        foreach (var file in files)
        {
            await store.EnqueueAsync(file, ContentWorkKind.Extract, 5, Ct);
        }

        (await scheduler.RunOnceAsync(Ct)).Should().Be(5);

        var hits = await Collect(store.FullTextAsync("\"invoice total\"", 100, Ct));
        hits.Select(h => Path.GetFileName(h.Path)).Should().BeEquivalentTo("invoice.pdf", "letter.docx", "mail.msg");
        (await store.GetDocumentStampAsync(files[4], Ct))!.Value.Status.Should().Be(ExtractStatus.Unsupported);
        (await store.ListQueueAsync(Ct)).Should().BeEmpty();
        (await store.GetTextAsync(Core.Vfs.VfsPath.FromWindowsPath(files[0]), DocTextKind.Content, Ct)).Should().Contain("1,234.56");

        // Unchanged files are skipped on re-enqueue; a modified file is re-extracted and the FTS index follows.
        await File.WriteAllTextAsync(Path.Combine(_dir, "notes.txt"), "first version mentions zebra", Ct);
        await store.EnqueueAsync(Path.Combine(_dir, "notes.txt"), ContentWorkKind.Extract, 5, Ct);
        await scheduler.RunOnceAsync(Ct);
        (await Collect(store.FullTextAsync("zebra", 10, Ct))).Should().ContainSingle();
        await File.WriteAllTextAsync(Path.Combine(_dir, "notes.txt"), "second version mentions giraffe", Ct);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "notes.txt"), DateTime.UtcNow.AddMinutes(1));
        await store.EnqueueAsync(Path.Combine(_dir, "notes.txt"), ContentWorkKind.Extract, 5, Ct);
        await scheduler.RunOnceAsync(Ct);
        (await Collect(store.FullTextAsync("zebra", 10, Ct))).Should().BeEmpty();
        (await Collect(store.FullTextAsync("giraffe", 10, Ct))).Should().ContainSingle();

        File.Delete(Path.Combine(_dir, "notes.txt"));
        await store.EnqueueAsync(Path.Combine(_dir, "notes.txt"), ContentWorkKind.Extract, 5, Ct);
        await scheduler.RunOnceAsync(Ct);
        (await Collect(store.FullTextAsync("giraffe", 10, Ct))).Should().BeEmpty("deleted files are removed from the content index");
    }

    [Fact]
    public async Task Failed_items_are_retried_with_exponential_backoff_up_to_five_attempts()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var store = new ContentStore(_factory, _writer, clock);
        var file = Path.Combine(_dir, "broken.bad");
        await File.WriteAllTextAsync(file, "x", Ct);
        var failing = new FailingExtractor();
        await using var scheduler = new ContentScheduler(store, [failing], new ContentOptions { RetryBaseDelay = TimeSpan.FromSeconds(30) });
        await store.EnqueueAsync(file, ContentWorkKind.Extract, 5, Ct);

        var expectedDelays = new[] { 30, 60, 120, 240 };
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await scheduler.RunOnceAsync(Ct);
            var item = (await store.ListQueueAsync(Ct)).Single();
            item.Attempts.Should().Be(attempt);
            DateTimeOffset.FromFileTime(item.NotBeforeUtc).Should().Be(clock.GetUtcNow().AddSeconds(expectedDelays[attempt - 1]));
            item.LastError.Should().Contain("simulated extractor failure");

            clock.Advance(TimeSpan.FromSeconds(expectedDelays[attempt - 1] - 1));
            (await scheduler.RunOnceAsync(Ct)).Should().Be(0, "the item is not ready before its backoff elapses");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        await scheduler.RunOnceAsync(Ct);
        failing.Calls.Should().Be(5);
        (await store.ListQueueAsync(Ct)).Should().BeEmpty("after the fifth failure the item is dropped");
        (await store.GetDocumentStampAsync(file, Ct))!.Value.Status.Should().Be(ExtractStatus.Failed);
    }

    [Fact]
    public async Task Pause_and_resume_are_honored()
    {
        var store = new ContentStore(_factory, _writer);
        var file = CopyFixtures("utf8.txt")[0];
        var policy = new SwitchPolicy();
        await using var scheduler = new ContentScheduler(store, Extractors, policy: policy);
        await store.EnqueueAsync(file, ContentWorkKind.Extract, 5, Ct);

        scheduler.Pause();
        scheduler.IsPaused.Should().BeTrue();
        (await scheduler.RunOnceAsync(Ct)).Should().Be(0);
        scheduler.Resume();
        policy.Reason = "Paused on battery power";
        scheduler.PauseReason.Should().Be("Paused on battery power");
        (await scheduler.RunOnceAsync(Ct)).Should().Be(0);
        policy.Reason = null;
        (await scheduler.RunOnceAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Background_workers_process_signalled_items()
    {
        var store = new ContentStore(_factory, _writer);
        var file = CopyFixtures("letter.docx")[0];
        await using var scheduler = new ContentScheduler(store, Extractors, new ContentOptions { Workers = 2 });
        scheduler.Start();

        await store.EnqueueAsync(file, ContentWorkKind.Extract, 0, Ct);
        scheduler.Signal();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (scheduler.Processed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        scheduler.Processed.Should().Be(1);
    }

    [Fact]
    public async Task Content_query_merges_with_memory_predicates()
    {
        var root = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(root);
        foreach (var file in new[] { "invoice.pdf", "other.pdf", "letter.docx", "utf8.txt" })
        {
            File.Copy(ContentFixtures.PathOf(file), Path.Combine(root, file));
        }

        var store = new ContentStore(_factory, _writer);
        await using var scheduler = new ContentScheduler(store, Extractors);
        foreach (var file in Directory.GetFiles(root))
        {
            await store.EnqueueAsync(file, ContentWorkKind.Extract, 5, Ct);
        }

        await scheduler.RunOnceAsync(Ct);
        await using var index = new IndexStore();
        var segment = await index.RegisterVolumeAsync(new IndexVolumeInfo(root + "\\", VolumeKind.NtfsFixed), 5, Ct);
        await new WalkerVolumeSource(root).WalkFreshAsync(index, segment, null, Ct);
        using var engine = new SearchEngine(index, content: store);

        var pdfOnly = await engine.ExecuteAsync(Request("content:\"invoice total\" ext:pdf"), Ct);
        var any = await engine.ExecuteAsync(Request("content:\"invoice total\""), Ct);
        var either = await engine.ExecuteAsync(Request("content:zzzz | ext:txt"), Ct);

        Names(index, pdfOnly).Should().BeEquivalentTo("invoice.pdf");
        Names(index, any).Should().BeEquivalentTo("invoice.pdf", "letter.docx", "utf8.txt");
        Names(index, either).Should().BeEquivalentTo("utf8.txt");
    }

    [Fact]
    public async Task Fts_queries_over_100k_documents_meet_the_budget()
    {
        var store = new ContentStore(_factory, _writer);
        var random = new Random(9);
        var vocabulary = Enumerable.Range(0, 30_000).Select(i => "w" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await _writer.ExecuteInTransactionAsync((c, t) =>
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
            var sb = new StringBuilder();
            for (var i = 1; i <= 100_000; i++)
            {
                sb.Clear();
                for (var w = 0; w < 60; w++)
                {
                    sb.Append(vocabulary[(int)(Math.Pow(random.NextDouble(), 3) * vocabulary.Length)]).Append(' '); // Zipf-like word frequencies
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
        }, Ct);

        var queries = new[] { "\"invoice total\"", "\"w1f4\"", "\"w7530\"", "\"w75\"*", "\"w3a98\" \"w12\"", "\"nothing_here\"" };
        var times = new List<double>();
        foreach (var round in Enumerable.Range(0, 6))
        {
            foreach (var q in queries)
            {
                var watch = Stopwatch.StartNew();
                var hits = await Collect(store.FullTextAsync(q, 200, Ct));
                watch.Stop();
                if (round > 0)
                {
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }

                if (q == "\"invoice total\"")
                {
                    hits.Should().HaveCount(100);
                }
            }
        }

        times.Sort();
        var p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
        TestContext.Current.SendDiagnosticMessage($"FTS5 over 100k documents: p50 {times[times.Count / 2]:F2} ms, p95 {p95:F2} ms (budget 50 ms)");
        Assert.SkipWhen(p95 > 50 && Shell.SystemLoad.IsOnBattery, $"FTS timing gate skipped on battery power (p95 {p95:F1} ms).");
        p95.Should().BeLessThanOrEqualTo(50);
    }

    private static SearchRequest Request(string query) => new(new QueryParser().Parse(query).Ast!, [], SortSpec.Unsorted);

    private static List<string> Names(IndexStore index, SearchResult result) =>
        result.Items.Select(e => index.CurrentGeneration.GetName(e).ToString()).ToList();

    private List<string> CopyFixtures(params string[] names) => names.Select(n =>
    {
        var target = Path.Combine(_dir, n);
        File.Copy(ContentFixtures.PathOf(n), target, overwrite: true);
        return target;
    }).ToList();

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private sealed class FailingExtractor : IContentExtractor
    {
        public int Calls { get; private set; }

        public string Name => "failing";

        public bool CanExtract(string extension) => true;

        public Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct)
        {
            Calls++;
            throw new IOException("simulated extractor failure");
        }
    }

    private sealed class SwitchPolicy : IContentPausePolicy
    {
        public string? Reason { get; set; }

        public string? PauseReason() => Reason;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
