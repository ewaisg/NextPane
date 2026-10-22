using System.Runtime.InteropServices;

namespace NexPane.Search.Tests;

/// <summary>Timing gates are meaningless on a throttled laptop running on battery; they skip (correctness still runs).</summary>
internal static class PowerState
{
    public static bool OnBattery => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    public static void SkipTimingGateOnBattery() =>
        Assert.SkipWhen(OnBattery, "Timing gate skipped: the machine is running on battery (CPU throttled). Correctness assertions passed; run on AC power or use tests/NexPane.Benchmarks.");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
