using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;

namespace NexPane.Search.Live;

/// <summary>
/// Live result updates (§6.3, P3.26). The index writer raises <see cref="IIndexSnapshotSource.DeltaApplied"/>; the hub
/// copies the batch into a channel (never blocking the writer), a background loop evaluates each active live query's
/// predicate on the changed entries only, and accumulated deltas are published at most once per
/// <see cref="EmitInterval"/> (100 ms → ≤ 10 batches/s) per query. Directory renames under path-dependent predicates,
/// compactions/resets and queries with post-filters fall back to a full re-evaluation.
/// </summary>
public sealed class LiveQueryHub : IDisposable
{
    public static readonly TimeSpan EmitInterval = TimeSpan.FromMilliseconds(100);

    private readonly SearchEngine _engine;
    private readonly IIndexSnapshotSource _source;
    private readonly TimeProvider _clock;
    private readonly Channel<IndexDeltaBatch> _batches = Channel.CreateUnbounded<IndexDeltaBatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<LiveQuery> _queries = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _processor;
    private readonly Task _flusher;

    internal LiveQueryHub(SearchEngine engine, IIndexSnapshotSource source, TimeProvider clock)
    {
        _engine = engine;
        _source = source;
        _clock = clock;
        _source.DeltaApplied += OnDeltaApplied;
        _processor = Task.Run(() => ProcessAsync(_stop.Token));
        _flusher = Task.Run(() => FlushLoopAsync(_stop.Token));
    }

    public int ActiveQueries
    {
        get
        {
            lock (_gate)
            {
                return _queries.Count;
            }
        }
    }

    public ILiveQuery Subscribe(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new LiveQuery(this, request);
        lock (_gate)
        {
            _queries.Add(query);
        }

        query.Initialize(_engine, _source.Current, _stop.Token);
        return query;
    }

    public void Dispose()
    {
        _source.DeltaApplied -= OnDeltaApplied;
        _stop.Cancel();
        _batches.Writer.TryComplete();
        try
        {
            Task.WaitAll([_processor, _flusher], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Normal shutdown of the background loops.
            Debug.Assert(ex.InnerExceptions.Count > 0, "cancelled loops");
        }

        lock (_gate)
        {
            foreach (var q in _queries.ToList())
            {
                q.Complete();
            }

            _queries.Clear();
        }

        _stop.Dispose();
    }

    internal void Remove(LiveQuery query)
    {
        lock (_gate)
        {
            _queries.Remove(query);
        }
    }

    private void OnDeltaApplied(object? sender, IndexDeltaBatch batch) => _batches.Writer.TryWrite(batch);

    private async Task ProcessAsync(CancellationToken ct)
    {
        var reader = _batches.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Coalesce everything queued so far into one evaluation pass.
                var batches = new List<IndexDeltaBatch>();
                while (reader.TryRead(out var batch))
                {
                    batches.Add(batch);
                }

                var snapshot = _source.Current;
                List<LiveQuery> queries;
                lock (_gate)
                {
                    queries = [.. _queries];
                }

                foreach (var query in queries)
                {
                    query.Apply(_engine, batches, snapshot, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(EmitInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                List<LiveQuery> queries;
                lock (_gate)
                {
                    queries = [.. _queries];
                }

                foreach (var query in queries)
                {
                    query.Flush(_clock);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            timer.Dispose();
        }
    }

    /// <summary>One subscription: membership set plus pending (not yet emitted) changes.</summary>
    internal sealed class LiveQuery : ILiveQuery
    {
        private readonly LiveQueryHub _hub;
        private readonly SearchRequest _request;
        private readonly Channel<ResultDelta> _deltas = Channel.CreateUnbounded<ResultDelta>(new UnboundedChannelOptions { SingleReader = false });
        private readonly Lock _gate = new();
        private HashSet<EntryRef> _members = [];
        private QueryPlan? _plan;
        private Predicate? _predicate;
        private long _generation;
        private SearchResult _current;
        private readonly HashSet<EntryRef> _pendingAdded = [];
        private readonly HashSet<EntryRef> _pendingRemoved = [];
        private readonly HashSet<EntryRef> _pendingChanged = [];
        private bool _pendingReset;
        private long _lastEmitTicks;
        private int _disposed;
        private bool _regexAborted; // a slow-mode regex spent its budget: never evaluated again (§7.2)

        public LiveQuery(LiveQueryHub hub, SearchRequest request)
        {
            _hub = hub;
            _request = request;
            _current = SearchResult.Empty(0);
        }

        /// <summary>Number of deltas published (for the ≤ 10 batches/s check).</summary>
        public int EmittedBatches { get; private set; }

        public SearchResult Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public async IAsyncEnumerable<ResultDelta> Deltas([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var delta in _deltas.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return delta;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _hub.Remove(this);
                Complete();
            }
        }

        internal void Complete() => _deltas.Writer.TryComplete();

        internal void Initialize(SearchEngine engine, IIndexSnapshot snapshot, CancellationToken ct)
        {
            Reevaluate(engine, snapshot, emitReset: true, ct);
        }

        internal void Apply(SearchEngine engine, IReadOnlyList<IndexDeltaBatch> batches, IIndexSnapshot snapshot, CancellationToken ct)
        {
            if (_disposed != 0 || _predicate is null || _plan is null || _regexAborted)
            {
                return;
            }

            var needsFull = _plan.PostFilters.Count > 0 || batches.Any(b => b.ResetSegments.Count > 0);
            var changed = new HashSet<EntryRef>();
            foreach (var batch in batches)
            {
                foreach (var change in batch.Changes)
                {
                    changed.Add(change.Entry);
                    if (change.Kind == IndexChangeKind.Renamed && _predicate.DependsOnPaths && IsDirectory(snapshot, change.Entry))
                    {
                        needsFull = true;
                    }
                }
            }

            if (needsFull)
            {
                Reevaluate(engine, snapshot, emitReset: batches.Any(b => b.ResetSegments.Count > 0), ct);
                return;
            }

            if (changed.Count == 0)
            {
                return;
            }

            if (_predicate.DependsOnPaths)
            {
                _predicate.Prepare(snapshot, ct);
            }

            var predicate = _predicate;
            lock (_gate)
            {
                foreach (var entry in changed)
                {
                    var matches = IsLive(snapshot, entry) && predicate.Match(snapshot.GetSegment(entry.Segment), entry.Index);
                    var member = _members.Contains(entry);
                    if (matches && !member)
                    {
                        _members.Add(entry);
                        if (!_pendingRemoved.Remove(entry))
                        {
                            _pendingAdded.Add(entry);
                        }
                        else
                        {
                            _pendingChanged.Add(entry);
                        }
                    }
                    else if (!matches && member)
                    {
                        _members.Remove(entry);
                        _pendingChanged.Remove(entry);
                        if (!_pendingAdded.Remove(entry))
                        {
                            _pendingRemoved.Add(entry);
                        }
                    }
                    else if (matches)
                    {
                        _pendingChanged.Add(entry);
                    }
                }

                _generation = snapshot.Generation;
                _current = BuildCurrent(snapshot);
            }

            if (_plan.RegexAborted)
            {
                AbortRegex(snapshot);
            }
        }

        internal void Flush(TimeProvider clock)
        {
            ResultDelta? delta = null;
            lock (_gate)
            {
                if (!_pendingReset && _pendingAdded.Count == 0 && _pendingRemoved.Count == 0 && _pendingChanged.Count == 0)
                {
                    return;
                }

                var now = clock.GetTimestamp();
                if (_lastEmitTicks != 0 && clock.GetElapsedTime(_lastEmitTicks, now) < EmitInterval)
                {
                    return;
                }

                delta = new ResultDelta(_generation, [.. _pendingAdded], [.. _pendingRemoved], [.. _pendingChanged], _pendingReset,
                    _pendingReset ? _current : null);
                _pendingAdded.Clear();
                _pendingRemoved.Clear();
                _pendingChanged.Clear();
                _pendingReset = false;
                _lastEmitTicks = now;
                EmittedBatches++;
            }

            _deltas.Writer.TryWrite(delta);
        }

        private void Reevaluate(SearchEngine engine, IIndexSnapshot snapshot, bool emitReset, CancellationToken ct)
        {
            if (_regexAborted)
            {
                return; // the pathological pattern is not re-run on later batches or resets
            }

            var watch = Stopwatch.StartNew();
            var plan = engine.Plan(_request with { RefinementOf = null });
            List<EntryRef> items;
            var warnings = new List<string>(plan.Warnings);
            if (plan.HasErrors)
            {
                items = [];
                warnings.AddRange(plan.Diagnostics.Select(d => d.Message));
            }
            else if (plan.PostFilters.Count > 0 || plan.FullText.Count > 0 || plan.Semantic.Count > 0 || plan.Tags.Count > 0)
            {
                var result = engine.ExecuteAsync(_request with { RefinementOf = null }, ct).GetAwaiter().GetResult();
                items = [.. result.Items];
                warnings = [.. result.Warnings];
            }
            else
            {
                items = engine.Evaluate(plan, _request with { RefinementOf = null }, snapshot, ct);
            }

            if (plan.RegexAborted || warnings.Contains(RegexFactory.AbortedMessage)) // the second form: aborted inside ExecuteAsync
            {
                _regexAborted = true;
                items = [];
                if (!warnings.Contains(RegexFactory.AbortedMessage))
                {
                    warnings.Add(RegexFactory.AbortedMessage);
                }
            }

            var predicate = SearchEngine.WithScope(plan.Memory, _request.Scope);
            predicate.Prepare(snapshot, ct);
            lock (_gate)
            {
                var next = new HashSet<EntryRef>(items);
                if (emitReset)
                {
                    _pendingAdded.Clear();
                    _pendingRemoved.Clear();
                    _pendingChanged.Clear();
                    _pendingReset = true;
                }
                else
                {
                    foreach (var e in next)
                    {
                        if (!_members.Contains(e) && !_pendingRemoved.Remove(e))
                        {
                            _pendingAdded.Add(e);
                        }
                    }

                    foreach (var e in _members)
                    {
                        if (!next.Contains(e) && !_pendingAdded.Remove(e))
                        {
                            _pendingRemoved.Add(e);
                        }
                    }
                }

                _members = next;
                _plan = plan;
                _predicate = predicate;
                _generation = snapshot.Generation;
                SearchEngine.Sort(items, _request.Sort, snapshot);
                _current = new SearchResult(items, snapshot.Generation, watch.Elapsed, false, warnings);
            }
        }

        /// <summary>The regex budget ran out while applying a delta batch: drop every member and keep the diagnostic.</summary>
        private void AbortRegex(IIndexSnapshot snapshot)
        {
            lock (_gate)
            {
                _regexAborted = true;
                foreach (var e in _members)
                {
                    _pendingChanged.Remove(e);
                    if (!_pendingAdded.Remove(e))
                    {
                        _pendingRemoved.Add(e);
                    }
                }

                _members = [];
                _current = new SearchResult([], snapshot.Generation, TimeSpan.Zero, false, [.. _current.Warnings, RegexFactory.AbortedMessage]);
            }
        }

        private SearchResult BuildCurrent(IIndexSnapshot snapshot)
        {
            var items = _members.OrderBy(e => e.Segment).ThenBy(e => e.Index).ToList();
            SearchEngine.Sort(items, _request.Sort, snapshot);
            return new SearchResult(items, snapshot.Generation, TimeSpan.Zero, false, _current.Warnings);
        }

        private static bool IsDirectory(IIndexSnapshot snapshot, EntryRef e) =>
            e.Segment < snapshot.SegmentCount && e.Index < snapshot.Count(e.Segment) && snapshot.GetSegment(e.Segment).IsDirectory(e.Index);

        private static bool IsLive(IIndexSnapshot snapshot, EntryRef e) =>
            e.Segment >= 0 && e.Segment < snapshot.SegmentCount && e.Index > 0 && e.Index < snapshot.Count(e.Segment) &&
            (snapshot is not IndexGeneration g || g.HasSegment(e.Segment)) && !snapshot.GetSegment(e.Segment).IsDeleted(e.Index);
    }
}
