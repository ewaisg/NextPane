# NexPane.Benchmarks

BenchmarkDotNet suite for the ARCHITECTURE §17 performance budgets (task P3.35). The CI gate is 120 % of each budget.
Run it on the reference machine, on AC power, with nothing else running:

```powershell
dotnet run -c Release --project tests/NexPane.Benchmarks -- --filter *          # everything
dotnet run -c Release --project tests/NexPane.Benchmarks -- --filter *Search*   # filename search only
```

| Benchmark | Measures | Budget (§17) |
|---|---|---|
| `SearchBenchmarks.ColdLiteralSubstring1M` | Cold literal substring over 1M synthetic entries | p50 ≤ 5 ms, p95 ≤ 15 ms |
| `SearchBenchmarks.IncrementalRefinement1M` | One keystroke refinement (`photo_0` → `photo_00`) over 1M entries | p50 ≤ 1 ms, p95 ≤ 3 ms |
| `SearchBenchmarks.RegexRedos1M` | `regex:/(a+)+$/` (NonBacktracking) over 1M names | p95 ≤ 150 ms |
| `SearchBenchmarks.PathDirectorySet1M` | `path:` substring through the directory-set evaluation over 1M entries | ≤ 50 ms |
| `LargeSearchBenchmarks.ColdLiteralSubstring5M` | Cold literal substring over 5M entries | p95 ≤ 60 ms |
| `PathReconstructionBenchmarks.GetFullPath1M` | 1M `GetFullPath` calls (directory path LRU) | ≤ 400 ms |
| `SnapshotLoadBenchmarks.Load1M` | Snapshot load of 1M entries from index.db | ≤ 2 s |
| `ContentSearchBenchmarks.FullText100k` | FTS5 phrase / term / prefix query over 100k documents | p95 ≤ 50 ms |

Covered by tests instead of BenchmarkDotNet:

| Budget | Where |
|---|---|
| Index memory ≤ 160 MB per 1M entries | `tests/NexPane.Indexing.Tests/IndexMemoryBudgetTests.cs` |
| Walker throughput ≥ 50k entries/s (warm) | `tests/NexPane.Indexing.Tests/WalkerAndWatcherTests.cs` |
| FS change → visible ≤ 1 s | `WalkerAndWatcherTests.Watcher_reflects_create_rename_delete_within_one_second` |
| USN enumeration ≤ 5 s / $MFT fill ≤ 10 s per 1M records | needs elevation: `[Elevated]` tests (P3.10, P3.11) |

BenchmarkDotNet reports mean, median and percentiles in `BenchmarkDotNet.Artifacts/results/` (git-ignored).
