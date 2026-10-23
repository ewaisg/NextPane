using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;
using NexPane.Storage.Settings;

namespace NexPane.Search;

/// <summary>Composition of the Search module (§4): implementations stay internal behind their Core interfaces.</summary>
public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IQueryParser"/>, <see cref="IQueryPrinter"/> and <see cref="ISearchEngine"/>. The engine reads
    /// the index through <see cref="IIndexSnapshotSource"/> (Indexing module) and uses the optional services that are
    /// registered: <see cref="IContentIndex"/>, <see cref="ISemanticSearch"/>, <see cref="ITagLookup"/>,
    /// <see cref="ICategoryResolver"/>, <see cref="TimeProvider"/>; its options come from the <c>search</c> section of
    /// <see cref="SettingsStore"/> when one is registered.
    /// </summary>
    public static IServiceCollection AddSearch(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<IQueryParser, QueryParser>();
        services.TryAddSingleton<IQueryPrinter, QueryPrinter>();
        services.TryAddSingleton<ISearchEngine>(sp => new SearchEngine(
            sp.GetRequiredService<IIndexSnapshotSource>(),
            sp.GetService<SettingsStore>() is { } settings ? SearchEngineOptions.FromSettings(settings) : new SearchEngineOptions(),
            sp.GetService<TimeProvider>(),
            sp.GetService<IContentIndex>(),
            sp.GetService<ISemanticSearch>(),
            sp.GetService<ITagLookup>(),
            sp.GetService<ICategoryResolver>()));
        return services;
    }
}
