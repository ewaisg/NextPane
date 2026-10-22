using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Core.Vfs;
using NexPane.Search.Live;

namespace NexPane.Search.Execution;

/// <summary>
/// Query execution (§6.2, P3.24/P3.25): plans the query, resolves FTS/semantic/tag lookups, scans the index generation
/// in parallel partitions (or only the previous result ids for an incremental refinement), applies post-filters
/// (accessed, content regex streaming scan, dupe) and sorts. Every stage observes the cancellation token.
/// </summary>
public sealed class SearchEngine : ISearchEngine, IDisposable
{
    private const int RefinementCacheSize = 16;
    private readonly IIndexSnapshotSource _source;
    private readonly SearchEngineOptions _options;
    private readonly TimeProvider _clock;
    private readonly IContentIndex? _content;
    private readonly ISemanticSearch? _semantic;
    private readonly ITagLookup? _tags;
    private readonly QueryPlanner _planner;
    private readonly LinkedList<(SearchRequest Request, SearchResult Result)> _recent = new();
    private readonly Lock _recentGate = new();
    private readonly LiveQueryHub _hub;
    private long _hashesComputed;
    private long _contentFilesScanned;

    public SearchEngine(
        IIndexSnapshotSource source,
        SearchEngineOptions? options = null,
        TimeProvider? clock = null,
        IContentIndex? content = null,
        ISemanticSearch? semantic = null,
        ITagLookup? tags = null,
        ICategoryResolver? categories = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new SearchEngineOptions();
        _clock = clock ?? TimeProvider.System;
        _content = content;
        _semantic = semantic;
        _tags = tags;
        _planner = new QueryPlanner(_options, _clock, categories);
        _hub = new LiveQueryHub(this, source, _clock);
    }

    /// <summary>SHA-256 computations done by <c>dupe:sha256</c> (only size-collision groups are hashed).</summary>
    public long HashesComputed => Interlocked.Read(ref _hashesComputed);

    /// <summary>Files read by <c>content:/regex/</c> scans.</summary>
    public long ContentFilesScanned => Interlocked.Read(ref _contentFilesScanned);

    public IIndexSnapshotSource Source => _source;

    public LiveQueryHub Hub => _hub;

    public QueryPlan Plan(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _planner.Plan(request.Query, request.RefinementOf?.Query);
    }

    public async Task<SearchResult> ExecuteAsync(SearchRequest request, CancellationToken ct)
    {
        SearchResult? last = null;
        await foreach (var partial in ExecuteProgressiveAsync(request, ct).ConfigureAwait(false))
        {
            last = partial;
        }

        return last ?? SearchResult.Empty(_source.Current.Generation);
    }

    /// <summary>Yields intermediate results while slow post-filters (content regex) run, then the final result.</summary>
    public async IAsyncEnumerable<SearchResult> ExecuteProgressiveAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        var snapshot = _source.Current;
        var plan = Plan(request);
        if (plan.HasErrors)
        {
            yield return new SearchResult([], snapshot.Generation, watch.Elapsed, false,
                plan.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message).ToList());
            yield break;
        }

        var warnings = new List<string>(plan.Warnings);
        await ResolveLookupsAsync(plan, warnings, ct).ConfigureAwait(false);
        var candidates = EvaluateCore(plan, request, snapshot, ct);

        if (plan.PostFilters.Count == 0)
        {
            var result = Finish(candidates, request, snapshot, watch, warnings);
            Remember(request, result);
            yield return result;
            yield break;
        }

        foreach (var filter in plan.PostFilters)
        {
            switch (filter)
            {
                case AccessedFilter accessed:
                    candidates = ApplyAccessed(candidates, accessed, snapshot, ct);
                    break;
                case DupeFilter dupe:
                    candidates = await ApplyDupeAsync(candidates, dupe, snapshot, ct).ConfigureAwait(false);
                    break;
                case ContentRegexFilter regex:
                {
                    var matched = new List<EntryRef>();
                    var lastYield = watch.Elapsed;
                    var capped = false;
                    var scanned = 0;
                    foreach (var entry in candidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (snapshot.GetSegment(entry.Segment).IsDirectory(entry.Index))
                        {
                            continue;
                        }

                        if (scanned >= _options.ContentRegexMaxFiles)
                        {
                            capped = true;
                            break;
                        }

                        scanned++;
                        if (await ContentMatchesAsync(snapshot, entry, regex.Regex, ct).ConfigureAwait(false))
                        {
                            matched.Add(entry);
                            if (watch.Elapsed - lastYield > TimeSpan.FromMilliseconds(100))
                            {
                                lastYield = watch.Elapsed;
                                yield return Finish(matched.ToList(), request, snapshot, watch, warnings, partial: true);
                            }
                        }
                    }

                    if (capped)
                    {
                        warnings.Add($"Content regex scan stopped after {_options.ContentRegexMaxFiles:N0} files (cap reached); narrow the query to scan the rest.");
                    }

                    candidates = matched;
                    break;
                }
            }
        }

        yield return Finish(candidates, request, snapshot, watch, warnings);
    }

    public ILiveQuery Subscribe(SearchRequest request) => _hub.Subscribe(request);

    public void Dispose() => _hub.Dispose();

    // ===================================================================== evaluation

    /// <summary>Evaluates the memory part of a plan (scope included) and returns matching refs in index order.</summary>
    public List<EntryRef> Evaluate(QueryPlan plan, SearchRequest request, IIndexSnapshot snapshot, CancellationToken ct) =>
        EvaluateCore(plan, request, snapshot, ct);

    private List<EntryRef> EvaluateCore(QueryPlan plan, SearchRequest request, IIndexSnapshot snapshot, CancellationToken ct)
    {
        var predicate = WithScope(plan.Memory, request.Scope);
        predicate.Prepare(snapshot, ct);
        if (plan.IsRefinement && request.RefinementOf is not null && TryRecall(request.RefinementOf, snapshot.Generation, out var previous))
        {
            return ScanCandidates(previous, predicate, snapshot, ct);
        }

        return ScanAll(predicate, snapshot, ct);
    }

    /// <summary>Combines the memory predicate with the request scope (subtree of any scope root).</summary>
    public static Predicate WithScope(Predicate memory, IReadOnlyList<VfsPath> scope)
    {
        if (scope.Count == 0)
        {
            return memory;
        }

        var roots = scope.Select(p => p.Scheme == VfsPath.FileScheme ? p.ToWindowsPath() : p.ToString()).ToList();
        var under = new UnderDirectoryPredicate(roots, directChildrenOnly: false, "scope");
        return new AndPredicate([memory, under]);
    }

    /// <summary>Parallel scan over all live entries of a generation in fixed-size partitions (§7.2).</summary>
    public List<EntryRef> ScanAll(Predicate predicate, IIndexSnapshot snapshot, CancellationToken ct)
    {
        var partitions = new List<(int Segment, int Start, int End)>();
        var size = Math.Max(1024, _options.PartitionSize);
        for (var s = 0; s < snapshot.SegmentCount; s++)
        {
            if (snapshot is IndexGeneration g && !g.HasSegment(s))
            {
                continue;
            }

            var count = snapshot.Count(s);
            for (var start = 1; start < count; start += size)
            {
                partitions.Add((s, start, Math.Min(count, start + size)));
            }
        }

        var results = new int[partitions.Count][];
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        var checkMask = CheckMask(predicate);
        Parallel.For(0, partitions.Count, parallel, p =>
        {
            var (s, start, end) = partitions[p];
            var segment = snapshot.GetSegment(s);
            var local = new List<int>();
            for (var i = start; i < end; i++)
            {
                if ((i & checkMask) == 0 && ct.IsCancellationRequested)
                {
                    return; // the caller throws once after the loop
                }

                if (!segment.IsDeleted(i) && predicate.Match(segment, i))
                {
                    local.Add(i);
                }
            }

            results[p] = [.. local];
        });
        ct.ThrowIfCancellationRequested();

        var total = results.Sum(r => r.Length);
        var refs = new List<EntryRef>(total);
        for (var p = 0; p < partitions.Count; p++)
        {
            var segmentId = partitions[p].Segment;
            foreach (var index in results[p])
            {
                refs.Add(new EntryRef(segmentId, index));
            }
        }

        return refs;
    }

    /// <summary>Evaluates only the given candidates (incremental refinement, live delta evaluation).</summary>
    public static List<EntryRef> ScanCandidates(IReadOnlyList<EntryRef> candidates, Predicate predicate, IIndexSnapshot snapshot, CancellationToken ct)
    {
        var list = candidates as EntryRef[] ?? (candidates as List<EntryRef>)?.ToArray() ?? [.. candidates];
        var checkMask = CheckMask(predicate);
        if (list.Length < 16_384)
        {
            var result = new List<EntryRef>(Math.Min(list.Length, 4096));
            EvaluateRange(list, 0, list.Length, predicate, snapshot, checkMask, result, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }

        var ranges = Math.Min(Environment.ProcessorCount * 2, (list.Length + 4095) / 4096);
        var rangeSize = (list.Length + ranges - 1) / ranges;
        var parts = new List<EntryRef>[ranges];
        Parallel.For(0, ranges, new ParallelOptions { CancellationToken = ct }, r =>
        {
            var local = new List<EntryRef>();
            EvaluateRange(list, r * rangeSize, Math.Min(list.Length, (r + 1) * rangeSize), predicate, snapshot, checkMask, local, ct);
            parts[r] = local;
        });
        ct.ThrowIfCancellationRequested();

        var merged = new List<EntryRef>(parts.Sum(p => p.Count));
        foreach (var part in parts)
        {
            merged.AddRange(part);
        }

        return merged;
    }

    /// <summary>How often scans poll the cancellation token: every 64 entries for expensive predicates, else 1 024.</summary>
    private static int CheckMask(Predicate predicate) => predicate.Cost >= 20 ? 63 : 1023;

    private static void EvaluateRange(EntryRef[] candidates, int start, int end, Predicate predicate, IIndexSnapshot snapshot, int checkMask, List<EntryRef> output, CancellationToken ct)
    {
        IndexSegment? segment = null;
        var segmentId = -1;
        var count = 0;
        for (var k = start; k < end; k++)
        {
            if ((k & checkMask) == 0 && ct.IsCancellationRequested)
            {
                return; // callers throw once
            }

            var e = candidates[k];
            if (e.Segment != segmentId)
            {
                segmentId = e.Segment;
                var valid = segmentId >= 0 && segmentId < snapshot.SegmentCount && (snapshot is not IndexGeneration g || g.HasSegment(segmentId));
                segment = valid ? snapshot.GetSegment(segmentId) : null;
                count = valid ? snapshot.Count(segmentId) : 0;
            }

            if (segment is not null && e.Index > 0 && e.Index < count && !segment.IsDeleted(e.Index) && predicate.Match(segment, e.Index))
            {
                output.Add(e);
            }
        }
    }

    private static bool IsLive(IIndexSnapshot snapshot, EntryRef e) =>
        e.Segment >= 0 && e.Segment < snapshot.SegmentCount && e.Index > 0 && e.Index < snapshot.Count(e.Segment) &&
        (snapshot is not IndexGeneration g || g.HasSegment(e.Segment)) && !snapshot.GetSegment(e.Segment).IsDeleted(e.Index);

    // ===================================================================== lookups

    internal async Task ResolveLookupsAsync(QueryPlan plan, List<string> warnings, CancellationToken ct)
    {
        foreach (var lookup in plan.FullText)
        {
            if (_content is null)
            {
                warnings.Add("Content search is unavailable (the content index is not running).");
                continue;
            }

            var paths = new List<string>();
            await foreach (var hit in _content.FullTextAsync(lookup.FtsQuery, _options.MaxFullTextHits, ct).ConfigureAwait(false))
            {
                paths.Add(hit.Path);
            }

            if (paths.Count >= _options.MaxFullTextHits)
            {
                warnings.Add($"Content query matched more than {_options.MaxFullTextHits:N0} documents; results are truncated.");
            }

            lookup.Target.Set = new EntryKeySet([], paths);
        }

        foreach (var lookup in plan.Semantic)
        {
            if (_semantic is null || !_semantic.IsAvailable)
            {
                warnings.Add("Semantic search needs an embedding-capable AI provider; configure one in Settings › AI.");
                continue;
            }

            var hits = await _semantic.SearchAsync(lookup.Text, _options.SemanticTopK, ct).ConfigureAwait(false);
            lookup.Target.Set = new EntryKeySet([], hits.Select(h => h.Path));
        }

        foreach (var lookup in plan.Tags)
        {
            if (_tags is null)
            {
                warnings.Add("Tags are unavailable.");
                continue;
            }

            lookup.Target.Set = await _tags.ResolveAsync(lookup.Tags, ct).ConfigureAwait(false);
        }
    }

    // ===================================================================== post-filters

    private static List<EntryRef> ApplyAccessed(List<EntryRef> candidates, AccessedFilter filter, IIndexSnapshot snapshot, CancellationToken ct)
    {
        var result = new List<EntryRef>();
        foreach (var e in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var segment = snapshot.GetSegment(e.Segment);
            if (segment.Volume.Separator != '\\')
            {
                continue; // remote entries have no access time
            }

            var path = segment.GetFullPath(e.Index);
            try
            {
                var info = new FileInfo(path);
                var accessed = segment.IsDirectory(e.Index) ? Directory.GetLastAccessTimeUtc(path) : info.LastAccessTimeUtc;
                if (accessed.Year > 1601 && filter.Set.Contains(accessed.ToFileTimeUtc()))
                {
                    result.Add(e);
                }
            }
            catch (IOException)
            {
                // Vanished or inaccessible since indexing: not a match.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        return result;
    }

    private async Task<bool> ContentMatchesAsync(IIndexSnapshot snapshot, EntryRef entry, UserRegex regex, CancellationToken ct)
    {
        var segment = snapshot.GetSegment(entry.Segment);
        var path = segment.GetFullPath(entry.Index);
        Interlocked.Increment(ref _contentFilesScanned);
        string? text = null;
        if (_content is not null && VfsPath.TryParse(path, out var vfs))
        {
            text = await _content.GetTextAsync(vfs, DocTextKind.Content, ct).ConfigureAwait(false);
        }

        if (text is null && segment.Volume.Separator == '\\' && segment.GetSize(entry.Index) <= _options.ContentRegexMaxBytesPerFile)
        {
            text = await ReadTextAsync(path, ct).ConfigureAwait(false);
        }

        if (text is null)
        {
            return false;
        }

        var timeouts = 0;
        return regex.IsMatch(text, ref timeouts);
    }

    /// <summary>Reads a file as text (BOM / UTF-8 detection); binary files (NUL in the first 8 KB) yield null.</summary>
    internal static async Task<string?> ReadTextAsync(string path, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var head = new byte[Math.Min(8192, stream.Length)];
                var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                var isUtf16 = read >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));
                if (!isUtf16 && head.AsSpan(0, read).Contains((byte)0))
                {
                    return null;
                }

                stream.Position = 0;
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, 65536, leaveOpen: true);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<List<EntryRef>> ApplyDupeAsync(List<EntryRef> candidates, DupeFilter filter, IIndexSnapshot snapshot, CancellationToken ct)
    {
        IEnumerable<IGrouping<string, EntryRef>> groups = candidates
            .Where(e => !snapshot.GetSegment(e.Segment).IsDirectory(e.Index) || filter.Keys == DupeKeys.Name)
            .GroupBy(e =>
            {
                var segment = snapshot.GetSegment(e.Segment);
                var key = new StringBuilder();
                if ((filter.Keys & DupeKeys.Name) != 0)
                {
                    key.Append(segment.GetFoldedName(e.Index)).Append('\u0001');
                }

                if ((filter.Keys & DupeKeys.Size) != 0)
                {
                    key.Append(segment.GetSize(e.Index));
                }

                return key.ToString();
            }, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        var result = new List<EntryRef>();
        foreach (var group in groups)
        {
            if ((filter.Keys & DupeKeys.Sha256) == 0)
            {
                result.AddRange(group);
                continue;
            }

            // Hash only size-collision groups (the grouping above already includes the size).
            var byHash = new Dictionary<string, List<EntryRef>>(StringComparer.Ordinal);
            foreach (var e in group)
            {
                ct.ThrowIfCancellationRequested();
                var hash = await HashAsync(snapshot.GetSegment(e.Segment).GetFullPath(e.Index), ct).ConfigureAwait(false);
                if (hash is null)
                {
                    continue;
                }

                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = [];
                    byHash[hash] = list;
                }

                list.Add(e);
            }

            result.AddRange(byHash.Values.Where(l => l.Count > 1).SelectMany(l => l));
        }

        result.Sort((a, b) => a.Segment != b.Segment ? a.Segment.CompareTo(b.Segment) : a.Index.CompareTo(b.Index));
        return result;
    }

    private async Task<string?> HashAsync(string path, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _hashesComputed);
                return Convert.ToHexString(hash);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ===================================================================== results

    private static SearchResult Finish(List<EntryRef> items, SearchRequest request, IIndexSnapshot snapshot, Stopwatch watch, List<string> warnings, bool partial = false)
    {
        Sort(items, request.Sort, snapshot);
        var truncated = false;
        if (request.Limit is int limit && items.Count > limit)
        {
            items = items.GetRange(0, limit);
            truncated = true;
        }

        var allWarnings = partial ? [.. warnings, "Searching file contents…"] : warnings.ToList();
        return new SearchResult(items, snapshot.Generation, watch.Elapsed, truncated, allWarnings);
    }

    /// <summary>Stable sort by the requested column (§6.2).</summary>
    public static void Sort(List<EntryRef> items, SortSpec sort, IIndexSnapshot snapshot)
    {
        if (sort.Column == SortColumn.None || items.Count < 2)
        {
            if (sort.Descending)
            {
                items.Reverse();
            }

            return;
        }

        var keyed = items.Select((e, i) => (Entry: e, Order: i)).ToArray();
        Comparison<(EntryRef Entry, int Order)> compare = sort.Column switch
        {
            SortColumn.Name => (a, b) => snapshot.GetFoldedName(a.Entry).SequenceCompareTo(snapshot.GetFoldedName(b.Entry)),
            SortColumn.Path => (a, b) => string.Compare(snapshot.GetFullPath(a.Entry), snapshot.GetFullPath(b.Entry), StringComparison.OrdinalIgnoreCase),
            SortColumn.Size => (a, b) => snapshot.Get(a.Entry).Size.CompareTo(snapshot.Get(b.Entry).Size),
            SortColumn.Modified => (a, b) => snapshot.Get(a.Entry).ModifiedUtc.CompareTo(snapshot.Get(b.Entry).ModifiedUtc),
            SortColumn.Created => (a, b) => snapshot.Get(a.Entry).CreatedUtc.CompareTo(snapshot.Get(b.Entry).CreatedUtc),
            SortColumn.Extension => (a, b) => snapshot.GetSegment(a.Entry.Segment).GetFoldedExtension(a.Entry.Index)
                .SequenceCompareTo(snapshot.GetSegment(b.Entry.Segment).GetFoldedExtension(b.Entry.Index)),
            _ => (a, b) => 0,
        };
        Array.Sort(keyed, (a, b) =>
        {
            var c = compare(a, b);
            if (sort.Descending)
            {
                c = -c;
            }

            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        for (var i = 0; i < keyed.Length; i++)
        {
            items[i] = keyed[i].Entry;
        }
    }

    private void Remember(SearchRequest request, SearchResult result)
    {
        lock (_recentGate)
        {
            _recent.AddFirst((request, result));
            while (_recent.Count > RefinementCacheSize)
            {
                _recent.RemoveLast();
            }
        }
    }

    private bool TryRecall(SearchRequest request, long generation, out IReadOnlyList<EntryRef> items)
    {
        lock (_recentGate)
        {
            foreach (var (r, result) in _recent)
            {
                if (ReferenceEquals(r, request) && result.Generation == generation && !result.Truncated)
                {
                    items = result.Items;
                    return true;
                }
            }
        }

        items = [];
        return false;
    }
}
