using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexPane.Core.Indexing;
using NexPane.Indexing.Content;
using NexPane.Indexing.Store;
using NexPane.Storage;

namespace NexPane.Indexing;

/// <summary>Composition of the Indexing module (§4): implementations stay internal behind their Core interfaces.</summary>
public static class IndexingServiceCollectionExtensions
{
    /// <summary>Configuration section of the in-memory index options (<c>Indexing:ChannelCapacity</c>, ...).</summary>
    public const string Section = "Indexing";

    /// <summary>
    /// Registers the in-memory index as <see cref="IIndexSnapshotSource"/> (singleton writer, §7.1) and the FTS5 content
    /// store as <see cref="IContentIndex"/>. The content store needs the <c>index.db</c> <see cref="SqliteConnectionFactory"/>
    /// and <see cref="SqliteWriter"/> registered as keyed services under <see cref="DatabaseKind.Index"/> (Storage wiring).
    /// </summary>
    public static IServiceCollection AddIndexing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = ReadOptions(configuration.GetSection(Section));
        services.TryAddSingleton(_ => new IndexStore(options));
        services.TryAddSingleton<IIndexSnapshotSource>(sp => sp.GetRequiredService<IndexStore>());
        services.TryAddSingleton<IContentIndex>(sp => new ContentStore(
            sp.GetRequiredKeyedService<SqliteConnectionFactory>(DatabaseKind.Index),
            sp.GetRequiredKeyedService<SqliteWriter>(DatabaseKind.Index),
            sp.GetService<TimeProvider>()));
        return services;
    }

    internal static IndexStoreOptions ReadOptions(IConfiguration section)
    {
        var defaults = new IndexStoreOptions();
        return new IndexStoreOptions(
            Int(section, nameof(IndexStoreOptions.ChannelCapacity)) ?? defaults.ChannelCapacity,
            Double(section, nameof(IndexStoreOptions.CompactionTombstoneRatio)) ?? defaults.CompactionTombstoneRatio,
            Int(section, nameof(IndexStoreOptions.CompactionMinimumEntries)) ?? defaults.CompactionMinimumEntries,
            Int(section, nameof(IndexStoreOptions.PathCacheCapacity)) ?? defaults.PathCacheCapacity);
    }

    private static int? Int(IConfiguration section, string key) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static double? Double(IConfiguration section, string key) =>
        double.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1 ? value : null;
}
