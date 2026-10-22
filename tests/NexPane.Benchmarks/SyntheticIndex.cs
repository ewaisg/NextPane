using NexPane.Core.Indexing;

namespace NexPane.Benchmarks;

/// <summary>Synthetic index source for benchmarks: 10 % directories, names ≈ 18 chars, 64-way locality like real trees.</summary>
public sealed class SyntheticIndex : IIndexSnapshotSource
{
    private SyntheticIndex(IndexGeneration generation) => Current = generation;

    public event EventHandler<IndexDeltaBatch>? DeltaApplied
    {
        add { }
        remove { }
    }

    public IIndexSnapshot Current { get; }

    public IndexGeneration Generation => (IndexGeneration)Current;

    public static SyntheticIndex Build(int count, int seed = 42)
    {
        var segment = new IndexSegment(0, new IndexVolumeInfo(@"D:\", VolumeKind.NtfsFixed));
        segment.Append(5, 0, [], IndexEntryState.Directory, 0x10, 0, 0, 0);
        var random = new Random(seed);
        var directories = new List<int> { 0 };
        var depths = new List<int> { 0 }; // realistic trees: depth capped at 12 levels
        string[] stems = ["report", "IMG", "invoice", "notes", "backup", "Screenshot", "project", "data", "readme", "photo", "src", "bin"];
        string[] exts = ["txt", "jpg", "pdf", "docx", "cs", "png", "zip", "mp3", "json", "log"];
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        for (var i = 0; i < count; i++)
        {
            int parent;
            var parentSlot = Math.Max(0, directories.Count - 64) + random.Next(Math.Min(64, directories.Count));
            if (random.Next(64) == 0 || depths[parentSlot] >= 12)
            {
                parentSlot = random.Next(directories.Count);
                while (depths[parentSlot] >= 12)
                {
                    parentSlot = random.Next(Math.Max(1, directories.Count / 8));
                }
            }

            parent = directories[parentSlot];

            if (i % 10 == 0)
            {
                directories.Add(segment.Append((ulong)(1000 + i), parent, $"{stems[random.Next(stems.Length)]}_dir{i}", IndexEntryState.Directory, 0x10, 0, baseTime + i, baseTime));
                depths.Add(depths[parentSlot] + 1);
            }
            else
            {
                segment.Append((ulong)(1000 + i), parent, $"{stems[random.Next(stems.Length)]}_{i:D7}.{exts[random.Next(exts.Length)]}", IndexEntryState.None, 0x20,
                    random.NextInt64(0, 1L << 30), baseTime + (random.NextInt64(0, 365) * TimeSpan.TicksPerDay), baseTime);
            }
        }

        segment.Publish();
        return new SyntheticIndex(new IndexGeneration(1, [segment], new IndexRemapLog()));
    }
}
