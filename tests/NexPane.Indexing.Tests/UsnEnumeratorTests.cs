using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using NexPane.Indexing.Ntfs;

namespace NexPane.Indexing.Tests;

/// <summary>
/// P3.38: the allocation-free <c>FSCTL_ENUM_USN_DATA</c> batch parser over the committed fixture and over a synthetic
/// 1M-record stream (the CPU part of the §17 "USN enumeration ≤ 5 s per 1M" budget; the IOCTL part is measured by the
/// elevated <c>ElevatedBudgetTests</c>).
/// </summary>
[Collection(nameof(RdcwWatcherStressGroup))] // timing: runs alone
public sealed class UsnEnumeratorTests
{
    [Fact]
    public void Batch_parser_reads_the_enumeration_fixture_in_place()
    {
        var path = Path.Combine(Path.GetDirectoryName(ContentFixtures.Directory)!, "usn", "enum-v2.bin");
        var entries = new List<(ulong Frn, ulong Parent, uint Attributes, string Name)>();

        var count = UsnEnumerator.ParseBatch(File.ReadAllBytes(path), (frn, parent, attributes, name) => entries.Add((frn, parent, attributes, name.ToString())));

        count.Should().Be(2);
        entries.Should().Equal(
            (0x0005000000000025UL, 0x0005000000000005UL, 0x10u, "Users"),
            (0x0003000000000040UL, 0x0005000000000025UL, 0x20u, "notes.txt"));
    }

    [Fact]
    public void Malformed_records_are_rejected()
    {
        var buffer = Batch(1, 0, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 12); // record length below the header
        var act = () => UsnEnumerator.ParseBatch(buffer, (_, _, _, _) => { });
        act.Should().Throw<InvalidDataException>();

        buffer = Batch(1, 0, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8 + 58), 200); // name offset beyond the record
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void One_million_records_parse_and_fill_names_and_hierarchy_well_within_the_budget()
    {
        const int total = 1_000_000;
        var batch = Batch(43_000, 0, out var perBatch);
        var parents = new Dictionary<ulong, ulong>(total);
        var arena = new char[32 << 20];
        var used = 0;
        var offset = 0UL;
        void Fill(ulong frn, ulong parent, uint attributes, ReadOnlySpan<char> name)
        {
            parents[frn + offset] = parent;
            name.CopyTo(arena.AsSpan(used));
            used += name.Length;
        }

        UsnEnumerator.ParseBatch(batch, Fill); // warm-up (JIT)
        parents.Clear();
        used = 0;
        var watch = Stopwatch.StartNew();
        long records = 0;
        for (var b = 0; records < total; b++)
        {
            offset = (ulong)b * (ulong)perBatch;
            records += UsnEnumerator.ParseBatch(batch, Fill);
        }

        watch.Stop();
        var perMillion = watch.Elapsed.TotalSeconds / (records / 1_000_000.0);
        TestContext.Current.SendDiagnosticMessage($"USN batch parse + fill: {records:N0} records in {watch.Elapsed.TotalSeconds:F3} s = {perMillion:F3} s per 1M");
        parents.Count.Should().Be((int)records);
        Assert.SkipWhen(perMillion > 1.0 && Shell.SystemLoad.IsOnBattery, $"Timing gate skipped on battery power ({perMillion:F2} s per 1M).");
        perMillion.Should().BeLessThan(1.0, "parsing must leave the 5 s per 1M budget to the IOCTL");
    }

    /// <summary>An <c>FSCTL_ENUM_USN_DATA</c> output with <paramref name="count"/> V2 records (8-byte next FRN first).</summary>
    private static byte[] Batch(int count, ulong firstFrn, out int records)
    {
        var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(firstFrn + (ulong)count + 1));
        for (var i = 0; i < count; i++)
        {
            var name = Encoding.Unicode.GetBytes($"document_{i:D7}.txt");
            var length = (60 + name.Length + 7) & ~7;
            var record = new byte[length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), firstFrn + (ulong)i + 100);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 5 + (ulong)(i % 1000));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), i % 10 == 0 ? 0x10u : 0x20u);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
            name.CopyTo(record, 60);
            stream.Write(record);
        }

        records = count;
        return stream.ToArray();
    }
}
