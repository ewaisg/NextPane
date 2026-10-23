using System.Runtime.InteropServices;
using NexPane.Shell.Threading;

namespace NexPane.Shell.Tests.Threading;

public sealed class ShellStaSchedulerTests
{
    [Fact]
    public async Task Work_runs_on_named_STA_threads_and_continuations_leave_them()
    {
        using var sta = new ShellStaScheduler(2);
        var ct = TestContext.Current.CancellationToken;

        var (apartment, name, threadId, isPool) = await sta.RunAsync(
            () => (Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.Name, Environment.CurrentManagedThreadId, sta.IsPoolThread), ct);

        apartment.Should().Be(ApartmentState.STA);
        name.Should().StartWith("NexPane Shell STA");
        isPool.Should().BeTrue();
        Environment.CurrentManagedThreadId.Should().NotBe(threadId, "results complete asynchronously, off the STA thread");
        sta.IsPoolThread.Should().BeFalse();
    }

    [Fact]
    public async Task Pool_runs_up_to_its_thread_count_concurrently()
    {
        using var sta = new ShellStaScheduler(3);
        using var barrier = new Barrier(3);
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable.Range(0, 3)
            .Select(_ => sta.RunAsync(() => barrier.SignalAndWait(TimeSpan.FromSeconds(10)), ct))
            .ToArray();

        (await Task.WhenAll(tasks)).Should().AllBeEquivalentTo(true, "three STA threads reached the barrier together");
    }

    [Fact]
    public async Task Queued_work_can_be_cancelled_and_exceptions_flow_to_the_caller()
    {
        using var sta = new ShellStaScheduler(2);
        using var gate = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var ct = TestContext.Current.CancellationToken;

        var blockers = Enumerable.Range(0, 2).Select(_ => sta.RunAsync(() => gate.Wait(TimeSpan.FromSeconds(10)), ct)).ToArray();
        var ran = false;
        var queued = sta.RunAsync(() => ran = true, cts.Token);
        await cts.CancelAsync();
        gate.Set();
        await Task.WhenAll(blockers);

        await ((Func<Task>)(() => queued)).Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();

        var failing = sta.RunAsync<int>(() => throw new InvalidOperationException("boom"), ct);
        await ((Func<Task>)(() => failing)).Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        (await sta.RunAsync(() => 42, ct)).Should().Be(42, "the thread keeps pumping after a failed item");
    }

    [Fact]
    public async Task Pool_threads_pump_window_messages_between_items()
    {
        using var sta = new ShellStaScheduler(2);
        var ct = TestContext.Current.CancellationToken;

        // A work item posts a message to its own thread; the next item that lands on that thread must find the queue
        // drained by the pump (a thread that only waits on a work queue would leave the message there forever).
        var target = await sta.RunAsync(
            () =>
            {
                PostThreadMessageW(GetCurrentThreadId(), 0x0400 /* WM_USER */, 0, 0).Should().BeTrue();
                return GetCurrentThreadId();
            },
            ct);

        var checkedTarget = false;
        for (var i = 0; i < 200 && !checkedTarget; i++)
        {
            var (threadId, postedPending) = await sta.RunAsync(
                () => (GetCurrentThreadId(), (GetQueueStatus(0x0008 /* QS_POSTMESSAGE */) >> 16) != 0 || PeekMessageW(out _, 0, 0x0400, 0x0400, 0)),
                ct);
            if (threadId == target)
            {
                postedPending.Should().BeFalse("the pump dispatched the posted message after the previous work item");
                checkedTarget = true;
            }
        }

        checkedTarget.Should().BeTrue();
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetQueueStatus(uint flags);
}
