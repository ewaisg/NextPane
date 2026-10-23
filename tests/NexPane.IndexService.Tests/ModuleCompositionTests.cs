using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Core.Vfs;
using NexPane.Indexing;
using NexPane.Search;
using NexPane.Shell;
using NexPane.Storage;

namespace NexPane.IndexService.Tests;

/// <summary>
/// P3.39: the module implementations are internal (architecture rule R4) and reachable only through their Core
/// interfaces, registered by <c>Add&lt;Module&gt;(IServiceCollection, IConfiguration)</c> (§4).
/// </summary>
public sealed class ModuleCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shell_indexing_and_search_compose_through_their_core_interfaces()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Indexing:ChannelCapacity"] = "1024",
        }).Build();
        var database = Path.Combine(Path.GetTempPath(), "nexpane-compose-" + Guid.NewGuid().ToString("N") + ".db");
        var services = new ServiceCollection().AddShell(configuration).AddIndexing(configuration).AddSearch(configuration);
        services.AddKeyedSingleton(DatabaseKind.Index, (_, _) => new SqliteConnectionFactory(database, DatabaseKind.Index)); // Storage wiring
        services.AddKeyedSingleton(DatabaseKind.Index, (sp, key) => new SqliteWriter(sp.GetRequiredKeyedService<SqliteConnectionFactory>(key)));
        try
        {
            await using var provider = services.BuildServiceProvider();
            await Compose(provider);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(database) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    private static async Task Compose(ServiceProvider provider)
    {
        var files = provider.GetRequiredService<IFileSystemProvider>();
        var index = provider.GetRequiredService<IIndexSnapshotSource>();
        var parser = provider.GetRequiredService<IQueryParser>();
        var printer = provider.GetRequiredService<IQueryPrinter>();
        var engine = provider.GetRequiredService<ISearchEngine>();

        var content = provider.GetRequiredService<IContentIndex>();
        new object[] { files, index, parser, printer, engine, content }.Should().AllSatisfy(s => s.GetType().IsPublic.Should().BeFalse("implementations are internal"));
        var parsed = parser.Parse("ext:txt report");
        parsed.HasErrors.Should().BeFalse();
        printer.Print(parsed.Ast!).Should().Be("ext:txt report");
        var result = await engine.ExecuteAsync(new SearchRequest(parsed.Ast!, [], SortSpec.Unsorted), Ct);
        result.Items.Should().BeEmpty("the new index is empty");
        index.Current.Generation.Should().BeGreaterThanOrEqualTo(0);
        files.Should().BeSameAs(provider.GetRequiredService<IFileSystemProvider>(), "singletons");
    }

    [Fact]
    public void Content_index_requires_the_keyed_index_database_from_storage_wiring()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddIndexing(configuration).BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IContentIndex>();

        resolve.Should().Throw<InvalidOperationException>("index.db is registered by the Storage composition (keyed DatabaseKind.Index)");
    }
}
