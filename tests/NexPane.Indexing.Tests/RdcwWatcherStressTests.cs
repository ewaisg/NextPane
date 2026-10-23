using System.Diagnostics;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Tests;

/// <summary>Runs alone so <see cref="RdcwWatcher.LiveBuffers"/> counts only this test's watchers.</summary>
[CollectionDefinition(nameof(RdcwWatcherStressGroup), DisableParallelization = true)]
public sealed class RdcwWatcherStressGroup;

/// <summary>
/// P3.16 (QA finding, P3.36): <see cref="RdcwWatcher.Dispose"/> races the I/O completion callback. 10 000 watchers are
/// started and disposed (at varying points: before the first completion, while completions arrive, twice) while another
/// thread keeps generating changes. A double free would corrupt the heap and crash the test host; a leak or a double
/// release shows in <see cref="RdcwWatcher.LiveBuffers"/>, which must return to its baseline exactly.
/// </summary>
[Collection(nameof(RdcwWatcherStressGroup))]
public sealed class RdcwWatcherStressTests : IDisposable
{
    private const int Watchers = 10_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexpane-rdcw-stress-" + Guid.NewGuid().ToString("N"));

    public RdcwWatcherStressTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Dispose_racing_completions_releases_every_watcher_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = RdcwWatcher.LiveBuffers;
        using var stop = new CancellationTokenSource();
        var changes = 0L;
        var churn = Task.Factory.StartNew(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                var path = Path.Combine(_root, $"f{i++ % 64}.txt");
                File.WriteAllText(path, "x");
                File.Delete(path);
                Interlocked.Increment(ref changes);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); // own thread: Parallel.For saturates the pool

        var completions = new Task[Watchers];
        var eventsSeen = 0L;
        var watch = Stopwatch.StartNew();
        try
        {
            Parallel.For(0, Watchers, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 4), CancellationToken = ct }, i =>
            {
                var watcher = new RdcwWatcher(_root, recursive: true, bufferSize: 4096);
                completions[i] = watcher.Events.Completion;
                switch (i % 4)
                {
                    case 0: // dispose with no read issued
                        break;
                    case 1: // dispose immediately after issuing (cancel while pending)
                        watcher.Start();
                        break;
                    case 2: // dispose while completions are arriving
                        watcher.Start();
                        // Until the first completion delivered events (more are in flight), at most 100 ms.
                        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 10);
                        var seen = false;
                        while (!seen && Stopwatch.GetTimestamp() < deadline)
                        {
                            while (watcher.Events.TryRead(out _))
                            {
                                seen = true;
                                Interlocked.Increment(ref eventsSeen);
                            }

                            Thread.Sleep(0);
                        }

                        break;
                    default: // dispose after a variable spin, twice
                        watcher.Start();
                        Thread.SpinWait(i % 2000);
                        watcher.Dispose();
                        break;
                }

                watcher.Dispose();

                // Dispose completes the writer under the gate, so nothing is written after this drain and
                // Completion (which waits for an empty channel) must resolve.
                while (watcher.Events.TryRead(out _))
                {
                    Interlocked.Increment(ref eventsSeen);
                }
            });
        }
        finally
        {
            await stop.CancelAsync();
            await churn;
        }

        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(30), ct);
        var released = Stopwatch.StartNew();
        while (RdcwWatcher.LiveBuffers != baseline && released.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10, ct);
        }

        TestContext.Current.SendDiagnosticMessage($"{Watchers} watchers in {watch.Elapsed.TotalSeconds:F1} s; {Interlocked.Read(ref changes)} file changes, {Interlocked.Read(ref eventsSeen)} events read");
        RdcwWatcher.LiveBuffers.Should().Be(baseline, "every native buffer is freed exactly once");
        Interlocked.Read(ref eventsSeen).Should().BePositive("completions raced the disposals");
    }
}
