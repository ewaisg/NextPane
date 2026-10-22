using System.Runtime.InteropServices;

namespace NexPane.Shell;

/// <summary>Power and CPU load readings used to pause background work (§5 content pipeline).</summary>
public sealed class SystemLoad
{
    private readonly Lock _gate = new();
    private long _lastIdle;
    private long _lastTotal;

    /// <summary>True when running on battery (AC line offline).</summary>
    public static bool IsOnBattery => GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;

    /// <summary>Total CPU busy percentage since the previous call (first call: since boot).</summary>
    public double SampleCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        lock (_gate)
        {
            var total = kernel + user; // kernel time includes idle time
            var deltaTotal = total - _lastTotal;
            var deltaIdle = idle - _lastIdle;
            _lastTotal = total;
            _lastIdle = idle;
            return deltaTotal <= 0 ? 0 : Math.Clamp(100.0 * (deltaTotal - deltaIdle) / deltaTotal, 0, 100);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
