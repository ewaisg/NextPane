using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Core.Vfs;
using NexPane.Search.Categories;
using NexPane.Search.Execution;
using NexPane.Search.Language;
using NexPane.Search.Tags;
using NexPane.Storage;

namespace NexPane.Search.Tests;

public sealed class TagAndCatalogTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexpane-tags-" + Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _factory = null!;
    private SqliteWriter _writer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "app.db"), DatabaseKind.App);
        DatabaseBootstrapper.Open(_factory);
        _writer = new SqliteWriter(_factory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnectionFactory.ClearPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Ntfs_tag_survives_rename_and_move_within_the_volume()
    {
        var index = new TestIndex(@"C:\", VolumeKind.NtfsFixed, serial: 0xCAFE);
        var file = index.Add(@"Docs\contract.pdf");
        var target = index.Add(@"Archive", directory: true);
        index.Publish();
        using var tags = new TagService(_factory, _writer, index);

        await tags.TagAsync(file, "urgent", Ct);
        TagService.FileKey(index.Current, file).Should().StartWith("ntfs:0000CAFE:0000000000000000");

        index.Segment.SetParent(file.Index, target.Index);
        index.Rename(file, "contract-signed.pdf");
        index.Publish([new IndexChange(IndexChangeKind.Renamed, file)]);

        (await tags.GetTagsAsync(file, Ct)).Should().Equal("urgent");
        using var engine = new SearchEngine(index, tags: tags);
        var result = await engine.ExecuteAsync(new SearchRequest(new QueryParser().Parse("tag:urgent").Ast!, [], SortSpec.Unsorted), Ct);
        result.Items.Should().ContainSingle().Which.Should().Be(file);
        index.PathOf(result.Items[0]).Should().Be(@"C:\Archive\contract-signed.pdf");
    }

    [Fact]
    public async Task Path_keyed_tags_are_repaired_on_rename_of_the_file_or_an_ancestor()
    {
        var index = new TestIndex(@"\\nas\share", VolumeKind.Smb);
        var file = index.Add(@"projects\alpha\plan.docx");
        var folder = index.Add(@"projects\alpha");
        index.Publish();
        using var tags = new TagService(_factory, _writer, index);
        await tags.TagAsync(file, "review", Ct);
        TagService.FileKey(index.Current, file).Should().Be(@"path:\\NAS\SHARE\PROJECTS\ALPHA\PLAN.DOCX");

        index.Rename(folder, "beta");
        index.Publish([new IndexChange(IndexChangeKind.Renamed, folder)]);
        await WaitForAsync(() => tags.Repairs >= 1);

        var assignment = (await tags.GetAssignmentsAsync("review", Ct)).Single();
        assignment.FileKey.Should().Be(@"path:\\NAS\SHARE\PROJECTS\BETA\PLAN.DOCX");
        assignment.LastPath.Should().Be(@"\\nas\share\projects\beta\plan.docx");
        (await tags.GetTagsAsync(file, Ct)).Should().Equal("review");

        index.Rename(file, "plan-v2.docx");
        index.Publish([new IndexChange(IndexChangeKind.Renamed, file)]);
        await WaitForAsync(() => tags.Repairs >= 2);
        (await tags.GetTagsAsync(file, Ct)).Should().Equal("review");

        using var engine = new SearchEngine(index, tags: tags);
        var result = await engine.ExecuteAsync(new SearchRequest(new QueryParser().Parse("tag:review").Ast!, [], SortSpec.Unsorted), Ct);
        result.Items.Should().ContainSingle().Which.Should().Be(file);
    }

    [Fact]
    public async Task Path_keyed_tags_resolve_after_restart()
    {
        var index = new TestIndex(@"\\nas\share", VolumeKind.Smb);
        var file = index.Add(@"a\b.txt");
        index.Publish();
        using (var first = new TagService(_factory, _writer, index))
        {
            await first.TagAsync(file, "keep", Ct);
        }

        using var tags = new TagService(_factory, _writer, index);
        await tags.InitializeAsync(Ct);
        index.Rename(file, "c.txt");
        index.Publish([new IndexChange(IndexChangeKind.Renamed, file)]);
        await WaitForAsync(() => tags.Repairs >= 1);

        (await tags.GetTagsAsync(file, Ct)).Should().Equal("keep");
    }

    [Fact]
    public async Task Tag_crud_and_untag()
    {
        var index = new TestIndex();
        var file = index.Add("x.txt");
        index.Publish();
        using var tags = new TagService(_factory, _writer, index);
        await tags.CreateTagAsync("Blue", "#0000FF", Ct);
        await tags.TagAsync(file, "blue", Ct);
        await tags.TagAsync(file, "red", Ct);

        (await tags.ListTagsAsync(Ct)).Select(t => (t.Name, t.Color)).Should().BeEquivalentTo([("Blue", "#0000FF"), ("red", "#808080")]);
        (await tags.GetTagsAsync(file, Ct)).Should().Equal("Blue", "red");

        await tags.UntagAsync(file, "red", Ct);
        (await tags.GetTagsAsync(file, Ct)).Should().Equal("Blue");
        await tags.DeleteTagAsync("Blue", Ct);
        (await tags.GetTagsAsync(file, Ct)).Should().BeEmpty("deleting a tag cascades to its assignments");
    }

    [Fact]
    public async Task Categories_crud_round_trip_and_feed_the_category_macro()
    {
        var catalog = new SearchCatalog(_factory, _writer);
        var parser = new QueryParser();
        var layout = await catalog.SaveLayoutAsync("Photos layout", [new ColumnSetting("name", 320), new ColumnSetting("size", 80, Visible: false)], Ct);
        var filter = await catalog.SaveFilterAsync("Big recent", [parser.Parse("size:>1MB").Ast!, parser.Parse("modified:thisyear").Ast!], Ct);
        var saved = await catalog.SaveCategoryAsync(new SearchCategoryDefinition(
            "Photos", parser.Parse("ext:jpg;png | type:image").Ast!, Icon: "camera", FilterId: filter.FilterId, LayoutId: layout.LayoutId,
            Sort: new SortSpec(SortColumn.Modified, Descending: true), Scope: [VfsPath.Parse(@"C:\Users")], SortOrder: 2), Ct);

        var reloaded = new SearchCatalog(_factory, _writer);
        await reloaded.LoadAsync(Ct);
        var category = (await reloaded.ListCategoriesAsync(Ct)).Single();
        category.Should().BeEquivalentTo(saved);
        category.QueryText.Should().Be("ext:jpg;png OR type:image");
        category.Sort.Should().Be(new SortSpec(SortColumn.Modified, true));
        category.Scope.Should().Equal(VfsPath.Parse(@"C:\Users"));
        (await reloaded.GetLayoutAsync("Photos layout", Ct))!.Columns.Should().Equal(new ColumnSetting("name", 320), new ColumnSetting("size", 80, false));
        var chain = (await reloaded.GetFilterAsync("Big recent", Ct))!;
        chain.Chain.Should().HaveCount(2);
        new QueryPrinter().Print(chain.Combined!).Should().Be("size:>1MB modified:thisyear");

        reloaded.TryGetCategoryAst("photos", out var ast).Should().BeTrue();
        var plan = new QueryPlanner(new SearchEngineOptions(), TimeProvider.System, reloaded).Plan(parser.Parse("category:Photos holiday").Ast!);
        plan.HasErrors.Should().BeFalse();
        ast.Should().Be(category.Query);

        await reloaded.SaveCategoryAsync(new SearchCategoryDefinition("Photos", parser.Parse("ext:heic").Ast!), Ct);
        (await reloaded.GetCategoryAsync("Photos", Ct))!.QueryText.Should().Be("ext:heic");
        (await reloaded.DeleteCategoryAsync("Photos", Ct)).Should().BeTrue();
        (await reloaded.ListCategoriesAsync(Ct)).Should().BeEmpty();
        (await reloaded.DeleteLayoutAsync("Photos layout", Ct)).Should().BeTrue();
        (await reloaded.DeleteFilterAsync("Big recent", Ct)).Should().BeTrue();
        (await reloaded.ListFiltersAsync(Ct)).Should().BeEmpty();
        (await reloaded.ListLayoutsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Recursive_category_references_are_rejected_when_saving()
    {
        var catalog = new SearchCatalog(_factory, _writer);
        var parser = new QueryParser();
        await catalog.SaveCategoryAsync(new SearchCategoryDefinition("A", parser.Parse("ext:pdf").Ast!), Ct);
        await catalog.SaveCategoryAsync(new SearchCategoryDefinition("B", parser.Parse("category:A report").Ast!), Ct);

        var cycle = () => catalog.SaveCategoryAsync(new SearchCategoryDefinition("A", parser.Parse("category:B | ext:doc").Ast!), Ct);
        (await cycle.Should().ThrowAsync<CategoryValidationException>()).Which.Message.Should().Contain("A → B → A");

        var self = () => catalog.SaveCategoryAsync(new SearchCategoryDefinition("C", parser.Parse("NOT category:C").Ast!), Ct);
        await self.Should().ThrowAsync<CategoryValidationException>();

        var unknown = () => catalog.SaveCategoryAsync(new SearchCategoryDefinition("D", parser.Parse("category:Nope").Ast!), Ct);
        (await unknown.Should().ThrowAsync<CategoryValidationException>()).Which.Message.Should().Contain("unknown category 'Nope'");

        (await catalog.GetCategoryAsync("A", Ct))!.QueryText.Should().Be("ext:pdf", "the rejected update was not stored");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }
}
