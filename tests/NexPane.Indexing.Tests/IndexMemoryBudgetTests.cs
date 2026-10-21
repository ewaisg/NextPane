using NexPane.Core.Indexing;
using NexPane.Indexing.Store;

namespace NexPane.Indexing.Tests;

[CollectionDefinition(nameof(MemoryMeasurementGroup), DisableParallelization = true)]
public sealed class MemoryMeasurementGroup;

/// <summary>Runs alone (no parallel tests allocate meanwhile) so the managed heap delta is attributable to the index.</summary>
[Collection(nameof(MemoryMeasurementGroup))]
public sealed class IndexMemoryBudgetTests
{
    [Fact]
    public async Task One_million_synthetic_entries_fit_the_memory_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        await using var store = new IndexStore();
        var handle = await store.RegisterVolumeAsync(new IndexVolumeInfo(@"D:\", VolumeKind.NtfsFixed), 5, ct);
        foreach (var batch in IndexStoreTests.SyntheticTree(1_000_000, 5).Chunk(8_192))
        {
            await store.SubmitAsync(handle.Id, batch, ct);
        }

        await store.FlushAsync(ct);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var after = GC.GetTotalMemory(forceFullCollection: true);

        store.CurrentGeneration.Count(handle.Id).Should().Be(1_000_001);
        var usedMb = (after - before) / (1024.0 * 1024.0);
        var estimateMb = store.EstimateMemoryBytes() / (1024.0 * 1024.0);
        TestContext.Current.SendDiagnosticMessage($"Index memory for 1M entries: measured {usedMb:F1} MB, structural estimate {estimateMb:F1} MB (budget 160 MB)");
        usedMb.Should().BeLessThanOrEqualTo(160, "ARCHITECTURE §17: index memory ≤ 160 MB per 1M entries (measured {0:F1} MB, estimate {1:F1} MB)",
            usedMb, estimateMb);
    }
}
