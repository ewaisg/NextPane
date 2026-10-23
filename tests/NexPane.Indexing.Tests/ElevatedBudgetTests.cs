using System.Diagnostics;
using NexPane.Indexing.Ntfs;
using NexPane.Shell;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Tests;

/// <summary>
/// P3.38: §17 indexing budgets measured on the system volume, scaled to its record count (budget per 1M records, CI gate
/// 120 %). The timings and counts are written through <see cref="ElevatedMetrics"/>; a gate that is exceeded on battery
/// power skips (CPU throttled) instead of failing, as in the default suite.
/// </summary>
[Trait("Category", "Elevated")]
[Collection(nameof(RdcwWatcherStressGroup))] // runs alone: no parallel I/O from other tests skews the timings
public sealed class ElevatedBudgetTests
{
    private const double CiGate = 1.2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SystemRoot => Path.GetPathRoot(Environment.SystemDirectory)!;

    [Fact]
    public void Usn_enumeration_of_the_system_volume_meets_the_budget()
    {
        using var volume = NtfsVolume.Open(SystemRoot);
        volume.TryQueryJournal(out var journal, out var error).Should().BeTrue($"the system volume has an active change journal (error {error})");
        var data = volume.GetVolumeData();
        var mftRecords = data.MftValidDataLength / data.BytesPerFileRecordSegment;

        var watch = Stopwatch.StartNew();
        long records = 0, directories = 0;
        foreach (var record in UsnEnumerator.Enumerate(volume, journal.NextUsn, Ct))
        {
            records++;
            if (record.IsDirectory)
            {
                directories++;
            }
        }

        watch.Stop();
        Gate("USN enumeration (FSCTL_ENUM_USN_DATA, names + hierarchy)", watch.Elapsed, records, budgetPerMillion: TimeSpan.FromSeconds(5),
            $"{directories:N0} directories; MFT size {mftRecords:N0} records");
        records.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Raw_mft_fill_of_the_system_volume_meets_the_budget()
    {
        using var volume = NtfsVolume.Open(SystemRoot);
        var data = volume.GetVolumeData();
        var mftRecords = data.MftValidDataLength / data.BytesPerFileRecordSegment;

        var watch = Stopwatch.StartNew();
        var stats = MftReader.ReadAll(volume, Ct);
        watch.Stop();

        var withSize = stats.Values.Count(s => !s.IsDirectory && s.Size > 0);
        Gate("Raw $MFT size/timestamp fill", watch.Elapsed, mftRecords, budgetPerMillion: TimeSpan.FromSeconds(10),
            $"{stats.Count:N0} in-use base records parsed, {withSize:N0} files with a size");
        stats.Values.Should().Contain(s => s.Name == "Windows" && s.IsDirectory);
    }

    private static void Gate(string what, TimeSpan elapsed, long records, TimeSpan budgetPerMillion, string details)
    {
        var budget = budgetPerMillion * (Math.Max(records, 1) / 1_000_000.0);
        var gate = budget * CiGate;
        var perMillion = elapsed / (Math.Max(records, 1) / 1_000_000.0);
        var power = SystemLoad.IsOnBattery ? "battery" : "AC";
        ElevatedMetrics.Write($"{what} on {SystemRoot}: {records:N0} records in {elapsed.TotalSeconds:F2} s = {perMillion.TotalSeconds:F2} s per 1M " +
                              $"(budget {budgetPerMillion.TotalSeconds:F0} s per 1M -> {budget.TotalSeconds:F2} s, gate {gate.TotalSeconds:F2} s; {power}); {details}");
        Assert.SkipWhen(elapsed > gate && SystemLoad.IsOnBattery, $"{what}: {elapsed.TotalSeconds:F2} s exceeds the {gate.TotalSeconds:F2} s gate on battery power (CPU throttled).");
        elapsed.Should().BeLessThanOrEqualTo(gate, "{0} for {1:N0} records must stay within 120 % of the §17 budget", what, records);
    }
}
