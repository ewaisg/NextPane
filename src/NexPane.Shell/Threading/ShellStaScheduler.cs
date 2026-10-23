using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NexPane.Shell.Threading;

/// <summary>
/// The Shell STA pool (ARCHITECTURE §5): 2–4 dedicated STA threads, each running a Win32 message pump
/// (<c>MsgWaitForMultipleObjectsEx</c> + <c>PeekMessage</c>/<c>DispatchMessage</c>) between work items, so COM objects
/// created by a work item can receive cross-apartment calls and window messages. A work item runs entirely on one
/// thread, so every COM object it creates is used and released in the same apartment (HANDOFF gotcha 1).
/// </summary>
internal sealed partial class ShellStaScheduler : TaskScheduler, IDisposable
{
    public const int MinThreads = 2;
    public const int MaxThreads = 4;

    private const uint Infinite = 0xFFFFFFFF;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint PmRemove = 0x0001;

    [ThreadStatic]
    private static ShellStaScheduler? s_current;

    private readonly ConcurrentQueue<Task> _queue = new();
    private readonly Semaphore _available = new(0, int.MaxValue);
    private readonly Thread[] _threads;
    private volatile bool _disposed;

    public ShellStaScheduler(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, MinThreads);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threadCount, MaxThreads);
        _threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = $"NexPane Shell STA {i + 1}",
            };
            thread.SetApartmentState(ApartmentState.STA);
            _threads[i] = thread;
            thread.Start();
        }
    }

    public override int MaximumConcurrencyLevel => _threads.Length;

    /// <summary>True when the calling thread is one of this pool's STA threads.</summary>
    public bool IsPoolThread => ReferenceEquals(s_current, this);

    /// <summary>
    /// Runs <paramref name="work"/> on one pool thread. The returned task completes asynchronously (continuations never
    /// run on the STA thread). Cancellation is honoured only while the item is still queued; once started it runs to completion.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var item = new StaWorkItem<T>(work, cancellationToken);
        new Task(static state => ((StaWorkItem<T>)state!).Run(), item, CancellationToken.None, TaskCreationOptions.DenyChildAttach)
            .Start(this);
        return item.Completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _available.Release(_threads.Length);
        var allStopped = true;
        foreach (var thread in _threads)
        {
            if (thread != Thread.CurrentThread && !thread.Join(TimeSpan.FromSeconds(5)))
            {
                allStopped = false;
            }
        }

        // A thread still inside a long shell call keeps waiting on the semaphore handle afterwards; leave it to finalization.
        if (allStopped && !IsPoolThread)
        {
            _available.Dispose();
        }
    }

    protected override void QueueTask(Task task)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queue.Enqueue(task);
        _available.Release();
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // Inline only on our own STA threads; a dequeued-but-already-inlined task is skipped by TryExecuteTask.
        return IsPoolThread && TryExecuteTask(task);
    }

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void Pump()
    {
        s_current = this;
        var handle = _available.SafeWaitHandle.DangerousGetHandle();
        while (!_disposed)
        {
            var wait = MsgWaitForMultipleObjectsEx(1, ref handle, Infinite, QsAllInput, MwmoInputAvailable);
            if (wait == WaitObject0)
            {
                // The semaphore count was decremented by the wait: exactly one queued item belongs to this thread.
                if (!_disposed && _queue.TryDequeue(out var task))
                {
                    TryExecuteTask(task);
                }
            }
            else if (wait == WaitFailed)
            {
                throw new InvalidOperationException($"MsgWaitForMultipleObjectsEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            }

            DispatchPendingMessages();
        }

        // Run anything still queued at shutdown so no caller is left waiting on a task that never completes.
        while (_queue.TryDequeue(out var orphan))
        {
            TryExecuteTask(orphan);
        }
    }

    private static void DispatchPendingMessages()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, PmRemove))
        {
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }
    }

    /// <summary>A queued call: completes its task exactly once, either cancelled while queued or with the work's outcome.</summary>
    private sealed class StaWorkItem<T>
    {
        private const int Queued = 0;
        private const int Started = 1;
        private const int Cancelled = 2;

        private readonly Func<T> _work;
        private readonly CancellationTokenRegistration _registration;
        private int _state;

        public StaWorkItem(Func<T> work, CancellationToken cancellationToken)
        {
            _work = work;
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.UnsafeRegister(
                    static (state, token) => ((StaWorkItem<T>)state!).CancelIfQueued(token), this);
            }
        }

        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _state, Started, Queued) != Queued)
            {
                return;
            }

            _registration.Dispose();
            try
            {
                Completion.TrySetResult(_work());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The caller observes the failure through the task; the STA thread keeps pumping.
                Completion.TrySetException(ex);
            }
        }

        private void CancelIfQueued(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _state, Cancelled, Queued) == Queued)
            {
                Completion.TrySetCanceled(token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint MsgWaitForMultipleObjectsEx(uint nCount, ref nint pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Msg lpMsg);
}
