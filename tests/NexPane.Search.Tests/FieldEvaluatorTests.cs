using NexPane.Core.Search;
using NexPane.Core.Vfs;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

public sealed class FieldEvaluatorTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    // "Now" = Wednesday 2026-10-21 12:00 local (Berlin, CEST = UTC+2).
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), Berlin);

    private static readonly TestIndex Fixture = BuildFixture();

    private static TestIndex BuildFixture()
    {
        var index = new TestIndex(@"C:\");
        DateTime Local(int y, int m, int d, int h = 12, int min = 0) =>
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Berlin);

        index.Add(@"Work\Report Q3.pdf", size: 2 * 1024 * 1024, modifiedUtc: Local(2026, 10, 21, 9));
        index.Add(@"Work\report-draft.docx", size: 15 * 1024, modifiedUtc: Local(2026, 10, 20, 23, 59));
        index.Add(@"Work\notes.txt", size: 0, modifiedUtc: Local(2026, 10, 19, 8));
        index.Add(@"Work\Archive\old.zip", size: 5L * 1024 * 1024 * 1024, modifiedUtc: Local(2025, 12, 31, 23, 30), createdUtc: Local(2025, 1, 1));
        index.Add(@"Photos\IMG_0001.jpg", size: 3 * 1024 * 1024, modifiedUtc: Local(2026, 10, 12));
        index.Add(@"Photos\IMG_0002.JPG", size: 4 * 1024 * 1024, modifiedUtc: Local(2026, 10, 1, 0, 30));
        index.Add(@"Photos\holiday.png", size: 200 * 1024 * 1024, modifiedUtc: Local(2026, 9, 30, 23, 59));
        index.Add(@"Photos\.thumbs", directory: true, attributes: 0x2);
        index.Add(@"Code\src\main.cs", size: 1200, modifiedUtc: Local(2026, 10, 14), attributes: 0x1);
        index.Add(@"Code\src\util.cs", size: 900, modifiedUtc: Local(2026, 10, 13));
        index.Add(@"Code\build.ps1", size: 300, modifiedUtc: Local(2026, 1, 15), attributes: 0x2 | 0x4);
        index.Add(@"Music\song.mp3", size: 8 * 1024 * 1024, modifiedUtc: Local(2026, 3, 29, 1, 30));
        index.Add(@"Music\R&D notes.txt", size: 10, modifiedUtc: Local(2026, 3, 29, 3, 30));
        index.Add(@"Fonts\Roboto.ttf", size: 100 * 1024, modifiedUtc: Local(2024, 7, 7));
        index.Add(@"setup.exe", size: 600 * 1024 * 1024, modifiedUtc: Local(2026, 10, 15));
        index.Add(@"Empty Folder", directory: true);
        index.Publish();
        return index;
    }

    public static TheoryData<string, string[]> Cases => new()
    {
        // name / bare / glob
        { "report", ["Report Q3.pdf", "report-draft.docx"] },
        { "REPORT", ["Report Q3.pdf", "report-draft.docx"] },
        { "\"report q3\"", ["Report Q3.pdf"] },
        { "name:notes", ["notes.txt", "R&D notes.txt"] },
        { "*.cs", ["main.cs", "util.cs"] },
        { "IMG_????.jpg", ["IMG_0001.jpg", "IMG_0002.JPG"] },
        { "img_*", ["IMG_0001.jpg", "IMG_0002.JPG"] },
        { "*.c", [] },
        { "\"*.cs\"", [] },
        { "R&D", ["R&D notes.txt"] },
        { "work", ["Work"] },
        // ext
        { "ext:pdf", ["Report Q3.pdf"] },
        { "ext:JPG", ["IMG_0001.jpg", "IMG_0002.JPG"] },
        { "ext:.cs", ["main.cs", "util.cs"] },
        { "ext:jpg;png", ["IMG_0001.jpg", "IMG_0002.JPG", "holiday.png"] },
        { "ext:thumbs", [] },
        // size
        { "size:empty", ["notes.txt"] },
        { "size:tiny", ["R&D notes.txt", "build.ps1", "util.cs", "main.cs", "report-draft.docx"] },
        { "size:small", ["Roboto.ttf"] },
        { "size:medium", ["Report Q3.pdf", "IMG_0001.jpg", "IMG_0002.JPG", "song.mp3"] },
        { "size:large", ["holiday.png", "setup.exe"] },
        { "size:huge", [] },
        { "size:gigantic", ["old.zip"] },
        { "size:>50MB", ["holiday.png", "setup.exe", "old.zip"] },
        { "size:>=4MB size:<=8MB", ["IMG_0002.JPG", "song.mp3"] },
        { "size:1KB..2MB", ["report-draft.docx", "Roboto.ttf", "main.cs", "Report Q3.pdf"] },
        { "size:1200", ["main.cs"] },
        { "size:=1200", ["main.cs"] },
        { "size:1.5MB..3MB", ["Report Q3.pdf", "IMG_0001.jpg"] },
        { "size:<1KB size:!=0", ["R&D notes.txt", "build.ps1", "util.cs"] },
        // dates (local time zone of the injected clock)
        { "modified:today", ["Report Q3.pdf"] },
        { "modified:yesterday", ["report-draft.docx"] },
        { "modified:thisweek", ["Report Q3.pdf", "report-draft.docx", "notes.txt"] },
        { "modified:lastweek", ["util.cs", "main.cs", "setup.exe", "IMG_0001.jpg"] },
        { "modified:thismonth ext:jpg", ["IMG_0001.jpg", "IMG_0002.JPG"] },
        { "modified:lastmonth", ["holiday.png"] },
        { "modified:lastyear", ["old.zip"] },
        { "modified:2026-10-01", ["IMG_0002.JPG"] },
        { "modified:2026-09", ["holiday.png"] },
        { "modified:<2026-01-01 type:file", ["old.zip", "Roboto.ttf"] },
        { "modified:>=2026-10-20 ext:docx;pdf", ["Report Q3.pdf", "report-draft.docx"] },
        { "modified:>2026-10-20 ext:docx;pdf", ["Report Q3.pdf"] },
        { "modified:<=2026-10-20 ext:docx;pdf", ["report-draft.docx"] },
        { "modified:2026-10-13..2026-10-14", ["util.cs", "main.cs"] },
        { "modified:last3d", ["Report Q3.pdf", "report-draft.docx", "notes.txt"] },
        { "modified:last2w ext:cs", ["util.cs", "main.cs"] },
        { "modified:2026-10-21T09:00", ["Report Q3.pdf"] },
        { "created:2025", ["old.zip"] },
        // attr
        { "attr:hidden", [".thumbs", "build.ps1"] },
        { "attr:readonly", ["main.cs"] },
        { "attr:system", ["build.ps1"] },
        { "attr:hidden;readonly", [".thumbs", "build.ps1", "main.cs"] },
        { "ext:cs attr:!readonly", ["util.cs"] },
        { "attr:h attr:s", ["build.ps1"] },
        // type
        { "type:image", ["IMG_0001.jpg", "IMG_0002.JPG", "holiday.png"] },
        { "type:code", ["main.cs", "util.cs", "build.ps1"] },
        { "type:executable", ["setup.exe", "build.ps1"] },
        { "type:font", ["Roboto.ttf"] },
        { "type:audio;archive", ["song.mp3", "old.zip"] },
        { "type:folder attr:hidden", [".thumbs"] },
        { "type:folder", ["Work", "Archive", "Photos", ".thumbs", "Code", "src", "Music", "Fonts", "Empty Folder"] },
        // depth / len
        { "depth:1 type:file", ["setup.exe"] },
        { "depth:3", ["old.zip", "main.cs", "util.cs"] },
        { "depth:>2 type:folder", [] },
        { "depth:2..2 type:folder", ["Archive", ".thumbs", "src"] },
        { "len:<6", ["Work", "Code", "Music", "Fonts", "src"] },
        { "len:>=17", ["report-draft.docx"] },
        // parent / path
        { "parent:C:\\Work", ["Report Q3.pdf", "report-draft.docx", "notes.txt", "Archive"] },
        { "parent:\"c:\\work\\\\\"", ["Report Q3.pdf", "report-draft.docx", "notes.txt", "Archive"] },
        { "parent:C:\\", ["Work", "Photos", "Code", "Music", "Fonts", "setup.exe", "Empty Folder"] },
        { "path:code\\src", ["src", "main.cs", "util.cs"] },
        { "path:\\src\\main", ["main.cs"] },
        { "path:work\\archive\\", ["old.zip"] },
        { "path:\"C:\\Music\" ext:mp3", ["song.mp3"] },
        { "path:/\\\\(src|Archive)\\\\[^\\\\]+$/", ["main.cs", "util.cs", "old.zip"] },
        // regex
        { "regex:/^IMG_\\d{4}\\.jpe?g$/", ["IMG_0001.jpg", "IMG_0002.JPG"] },
        { "regex:/^IMG_\\d{4}\\.jpe?g$/c", ["IMG_0001.jpg"] },
        { "regex:\"^r\"", ["Report Q3.pdf", "report-draft.docx", "R&D notes.txt", "Roboto.ttf"] },
        { "/s$/", ["Photos", ".thumbs", "Fonts", "main.cs", "util.cs"] },
        // boolean composition
        { "report -draft", ["Report Q3.pdf"] },
        { "ext:pdf | ext:docx", ["Report Q3.pdf", "report-draft.docx"] },
        { "(ext:jpg | ext:png) size:>3MB", ["IMG_0002.JPG", "holiday.png"] },
        { "NOT type:folder NOT ext:jpg;png;cs;pdf;docx;txt", ["old.zip", "build.ps1", "song.mp3", "Roboto.ttf", "setup.exe"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Evaluates(string query, string[] expectedNames)
    {
        var names = await RunAsync(query);

        names.Should().BeEquivalentTo(expectedNames, "query: {0}", query);
    }

    [Theory]
    [InlineData("size:>abc", "Invalid size")]
    [InlineData("modified:tomorrowish", "Invalid date")]
    [InlineData("modified:2026-02-30", "Invalid date")]
    [InlineData("depth:deep", "depth: takes a whole number")]
    [InlineData("attr:sparkly", "Unknown attribute 'sparkly'")]
    [InlineData("type:spreadsheets", "Unknown type 'spreadsheets'")]
    [InlineData("regex:/(unclosed/", "Invalid regular expression")]
    [InlineData("ext:pdf | content:/x/", "content: can only be combined with AND")]
    [InlineData("NOT accessed:today", "accessed: can only be combined with AND")]
    [InlineData("a | dupe:name", "dupe: can only be combined with AND")]
    [InlineData("dupe:color", "dupe: takes name, size or sha256")]
    [InlineData("category:Nope", "Unknown category 'Nope'")]
    public async Task Invalid_values_report_errors(string query, string message)
    {
        using var engine = new SearchEngine(Fixture, clock: Clock);
        var parsed = new QueryParser().Parse(query);
        var plan = new QueryPlanner(new SearchEngineOptions(), Clock).Plan(parsed.Ast!, spans: parsed.Spans);

        plan.Diagnostics.Should().Contain(d => d.Message.StartsWith(message, StringComparison.Ordinal));
        var result = await engine.ExecuteAsync(new SearchRequest(parsed.Ast!, [], SortSpec.Unsorted), TestContext.Current.CancellationToken);
        result.Items.Should().BeEmpty();
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void Diagnostics_of_invalid_terms_carry_the_term_span()
    {
        const string query = "ext:pdf size:>abc";
        var parsed = new QueryParser().Parse(query);

        var plan = new QueryPlanner(new SearchEngineOptions(), Clock).Plan(parsed.Ast!, spans: parsed.Spans);

        var span = plan.Diagnostics.Single().Span;
        query.Substring(span.Start, span.Length).Should().Be("size:>abc");
    }

    [Fact]
    public async Task Scope_restricts_results_to_subtrees()
    {
        using var engine = new SearchEngine(Fixture, clock: Clock);
        var ast = new QueryParser().Parse("type:file").Ast!;

        var result = await engine.ExecuteAsync(new SearchRequest(ast, [VfsPath.Parse(@"C:\Code"), VfsPath.Parse(@"C:\Music")], SortSpec.Unsorted), TestContext.Current.CancellationToken);

        result.Items.Select(e => Fixture.Generation.GetName(e).ToString()).Should().BeEquivalentTo("main.cs", "util.cs", "build.ps1", "song.mp3", "R&D notes.txt");
    }

    [Theory]
    [InlineData(SortColumn.Name, false, "build.ps1,main.cs,util.cs")]
    [InlineData(SortColumn.Name, true, "util.cs,main.cs,build.ps1")]
    [InlineData(SortColumn.Size, false, "build.ps1,util.cs,main.cs")]
    [InlineData(SortColumn.Modified, true, "main.cs,util.cs,build.ps1")]
    [InlineData(SortColumn.Path, false, "build.ps1,main.cs,util.cs")]
    public async Task Sorts_by_requested_column(SortColumn column, bool descending, string expected)
    {
        using var engine = new SearchEngine(Fixture, clock: Clock);
        var ast = new QueryParser().Parse("type:code").Ast!;

        var result = await engine.ExecuteAsync(new SearchRequest(ast, [], new SortSpec(column, descending)), TestContext.Current.CancellationToken);

        string.Join(',', result.Items.Select(e => Fixture.Generation.GetName(e).ToString())).Should().Be(expected);
    }

    [Fact]
    public async Task Limit_truncates_and_reports_it()
    {
        using var engine = new SearchEngine(Fixture, clock: Clock);

        var result = await engine.ExecuteAsync(new SearchRequest(new QueryParser().Parse("type:file").Ast!, [], SortSpec.Unsorted, Limit: 3), TestContext.Current.CancellationToken);

        result.Items.Should().HaveCount(3);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Day_boundaries_follow_dst_changes_of_the_local_zone()
    {
        // 2026-03-29: CET → CEST (02:00 → 03:00) — a 23-hour day. 2026-10-25: CEST → CET — a 25-hour day.
        DateParser.TryPeriod("2026-03-29", Clock, out var spring).Should().BeTrue();
        DateParser.TryPeriod("2026-10-25", Clock, out var autumn).Should().BeTrue();
        DateParser.TryPeriod("2026-10-24", Clock, out var normal).Should().BeTrue();

        TimeSpan.FromTicks(spring.High - spring.Low).Should().Be(TimeSpan.FromHours(23));
        TimeSpan.FromTicks(autumn.High - autumn.Low).Should().Be(TimeSpan.FromHours(25));
        TimeSpan.FromTicks(normal.High - normal.Low).Should().Be(TimeSpan.FromHours(24));
        DateTime.FromFileTimeUtc(spring.Low).Should().Be(new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc));
        DateTime.FromFileTimeUtc(autumn.Low).Should().Be(new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Today_on_a_dst_day_covers_the_local_day_only()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero), Berlin);
        using var engine = new SearchEngine(Fixture, clock: clock);

        var result = await engine.ExecuteAsync(new SearchRequest(new QueryParser().Parse("modified:today").Ast!, [], SortSpec.Unsorted), TestContext.Current.CancellationToken);

        result.Items.Select(e => Fixture.Generation.GetName(e).ToString()).Should().BeEquivalentTo("song.mp3", "R&D notes.txt");
    }

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("1B", 1L)]
    [InlineData("1KB", 1024L)]
    [InlineData("1kb", 1024L)]
    [InlineData("1K", 1024L)]
    [InlineData("2MB", 2L * 1024 * 1024)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("1TB", 1099511627776L)]
    [InlineData("0.5KiB", 512L)]
    public void Size_units_are_binary(string text, long bytes)
    {
        SizeParser.TryParseBytes(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MB")]
    [InlineData("1XB")]
    [InlineData("-1KB")]
    [InlineData("1,5MB")]
    public void Invalid_sizes_are_rejected(string text)
    {
        SizeParser.TryParseBytes(text, out _).Should().BeFalse();
    }

    private static async Task<List<string>> RunAsync(string query)
    {
        using var engine = new SearchEngine(Fixture, clock: Clock);
        var parsed = new QueryParser().Parse(query);
        parsed.HasErrors.Should().BeFalse();
        var result = await engine.ExecuteAsync(new SearchRequest(parsed.Ast!, [], SortSpec.Unsorted), TestContext.Current.CancellationToken);
        result.Warnings.Should().BeEmpty("query: {0}", query);
        return result.Items.Select(e => Fixture.Generation.GetName(e).ToString()).ToList();
    }
}
