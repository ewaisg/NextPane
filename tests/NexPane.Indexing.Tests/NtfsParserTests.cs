using System.Buffers.Binary;
using System.Text;
using NexPane.Indexing.Ntfs;

namespace NexPane.Indexing.Tests;

/// <summary>
/// USN record (P3.09) and raw MFT record (P3.11) parsers over binary fixtures in <c>tests/TestAssets/usn</c> and
/// <c>tests/TestAssets/mft</c>. The fixtures follow the documented on-disk layouts (winioctl.h USN_RECORD_V2/V3/V4,
/// NTFS FILE records with update-sequence fixups); <see cref="Regenerate_fixtures"/> rebuilds them.
/// </summary>
public sealed class NtfsParserTests
{
    private const int Sector = 512;
    private const int RecordSize = 1024;

    private static string AssetDir(string name) => Path.Combine(Path.GetDirectoryName(ContentFixtures.Directory)!, name);

    private static byte[] Asset(string dir, string file) => File.ReadAllBytes(Path.Combine(AssetDir(dir), file));

    [Fact]
    public void Regenerate_fixtures()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NEXPANE_REGENERATE_FIXTURES") == "1", "Fixtures are committed; set NEXPANE_REGENERATE_FIXTURES=1 to rebuild them.");
        Directory.CreateDirectory(AssetDir("usn"));
        Directory.CreateDirectory(AssetDir("mft"));
        File.WriteAllBytes(Path.Combine(AssetDir("usn"), "enum-v2.bin"), Buffer(0x0007000000000100,
            V2(0x0005000000000025, 0x0005000000000005, 0, "Users", 0x10, UsnReason.None),
            V2(0x0003000000000040, 0x0005000000000025, 0, "notes.txt", 0x20, UsnReason.None)));
        File.WriteAllBytes(Path.Combine(AssetDir("usn"), "journal-v3.bin"), Buffer(9001,
            V3(new UInt128(0, 0x0001000000000099), new UInt128(0, 0x0005000000000025), 8800, "new.docx", 0x20, UsnReason.FileCreate | UsnReason.Close),
            V3(new UInt128(0, 0x0001000000000099), new UInt128(0, 0x0005000000000025), 8900, "renamed.docx", 0x20, UsnReason.RenameNewName),
            V3(new UInt128(0x1234, 0xABCD), new UInt128(0, 5), 9000, "名前.txt", 0x20, UsnReason.FileDelete | UsnReason.Close)));
        File.WriteAllBytes(Path.Combine(AssetDir("usn"), "journal-v4.bin"), Buffer(500, V4(new UInt128(0, 77), new UInt128(0, 5), 450, UsnReason.DataOverwrite, [(0, 4096), (65536, 8192)])));
        File.WriteAllBytes(Path.Combine(AssetDir("mft"), "resident.bin"), MftRecord(100, 3, directory: false, names: [(5, "readme.txt", 3)], residentData: Encoding.ASCII.GetBytes("hello world!")));
        File.WriteAllBytes(Path.Combine(AssetDir("mft"), "nonresident.bin"), MftRecord(101, 7, directory: false, names: [(0x0002000000000040, "BIGFIL~1.ISO", 2), (0x0002000000000040, "big file.iso", 1)],
            nonResidentData: (5_000_000_123, [0x21, 0x40, 0x00, 0x10, 0x11, 0x10, 0x30, 0x01, 0x01, 0x00])));
        File.WriteAllBytes(Path.Combine(AssetDir("mft"), "attrlist.bin"), MftRecord(102, 1, directory: true, names: [(5, "Many Links", 1)],
            attributeList: [(0x30u, 102UL | (1UL << 48)), (0x80u, 2047UL | (4UL << 48))]));
    }

    [Fact]
    public void Parses_v2_enumeration_buffer()
    {
        var parsed = UsnRecordParser.ParseBuffer(Asset("usn", "enum-v2.bin"));

        parsed.Error.Should().BeNull();
        parsed.Next.Should().Be(0x0007000000000100);
        parsed.Records.Should().HaveCount(2);
        var dir = parsed.Records[0];
        dir.MajorVersion.Should().Be(2);
        dir.FileName.Should().Be("Users");
        dir.IsDirectory.Should().BeTrue();
        dir.FileReferenceLow.Should().Be(0x0005000000000025UL);
        dir.ParentReferenceLow.Should().Be(0x0005000000000005UL);
        parsed.Records[1].FileName.Should().Be("notes.txt");
        parsed.Records[1].ParentReferenceLow.Should().Be(0x0005000000000025UL);
    }

    [Fact]
    public void Parses_v3_journal_buffer_with_128_bit_ids_and_reasons()
    {
        var parsed = UsnRecordParser.ParseBuffer(Asset("usn", "journal-v3.bin"));

        parsed.Error.Should().BeNull();
        parsed.Next.Should().Be(9001);
        parsed.Records.Select(r => r.FileName).Should().Equal("new.docx", "renamed.docx", "名前.txt");
        parsed.Records[0].Reason.Should().Be(UsnReason.FileCreate | UsnReason.Close);
        parsed.Records[1].Usn.Should().Be(8900);
        parsed.Records[2].FileReference.Should().Be(new UInt128(0x1234, 0xABCD));
        parsed.Records[2].Reason.HasFlag(UsnReason.FileDelete).Should().BeTrue();
    }

    [Fact]
    public void Parses_v4_range_tracking_records()
    {
        var parsed = UsnRecordParser.ParseBuffer(Asset("usn", "journal-v4.bin"));

        parsed.Error.Should().BeNull();
        var record = parsed.Records.Should().ContainSingle().Subject;
        record.MajorVersion.Should().Be(4);
        record.FileName.Should().BeNull();
        record.Usn.Should().Be(450);
        record.Reason.Should().Be(UsnReason.DataOverwrite);
        record.Extents.Should().Equal(new UsnExtent(0, 4096), new UsnExtent(65536, 8192));
    }

    [Theory]
    [InlineData(0, 4, "Buffer shorter")]
    [InlineData(1, 0, "Invalid record length")]       // record length 0
    [InlineData(2, 0, "Invalid record length")]       // record length beyond buffer
    [InlineData(3, 0, "Invalid record length")]       // not 8-aligned
    [InlineData(4, 0, "Unsupported USN record version")]
    [InlineData(5, 0, "File name")]                   // name offset inside header
    [InlineData(6, 0, "File name")]                   // odd name length
    [InlineData(7, 0, "File name")]                   // name beyond record
    [InlineData(8, 0, "Invalid V4 extents")]
    public void Malformed_records_are_rejected_without_exceptions(int variant, int truncate, string message)
    {
        var record = variant == 8
            ? V4(new UInt128(0, 1), new UInt128(0, 2), 1, UsnReason.DataExtend, [(0, 1)])
            : V2(0x10, 0x5, 1, "file.txt", 0x20, UsnReason.FileCreate);
        switch (variant)
        {
            case 1: BinaryPrimitives.WriteUInt32LittleEndian(record, 0); break;
            case 2: BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length + 8); break;
            case 3: BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length - 3); break;
            case 4: BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 9); break;
            case 5: BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 20); break;
            case 6: BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), 7); break;
            case 7: BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), 400); break;
            case 8: BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(60), 50); break;
        }

        var span = record.AsSpan(0, record.Length - truncate);
        if (variant == 0)
        {
            span = record.AsSpan(0, truncate);
        }

        UsnRecordParser.TryParse(span, out var parsed, out _, out var error).Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().StartWith(message);
    }

    [Fact]
    public void Random_corruption_never_throws()
    {
        var valid = Asset("usn", "journal-v3.bin");
        var random = new Random(3);
        for (var i = 0; i < 20_000; i++)
        {
            var copy = (byte[])valid.Clone();
            for (var k = random.Next(1, 8); k > 0; k--)
            {
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            }

            var length = random.Next(copy.Length + 1);
            var act = () => UsnRecordParser.ParseBuffer(copy.AsSpan(0, length));
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Resident_record_fixups_names_and_data_size()
    {
        var bytes = Asset("mft", "resident.bin");

        MftRecordParser.TryParse(bytes, Sector, 100, out var record, out var error).Should().BeTrue(error);

        record!.InUse.Should().BeTrue();
        record.IsDirectory.Should().BeFalse();
        record.SequenceNumber.Should().Be(3);
        record.FileReference.Should().Be(100UL | (3UL << 48));
        record.PrimaryName!.Name.Should().Be("readme.txt");
        record.PrimaryName.ParentRecordNumber.Should().Be(5UL);
        record.DataSize.Should().Be(12);
        record.CreatedUtc.Should().Be(Times.Created);
        record.ModifiedUtc.Should().Be(Times.Modified);
        record.StandardAttributes.Should().Be(0x20u);
        record.SecurityId.Should().Be(0x105u);
        record.IsBaseRecord.Should().BeTrue();
    }

    [Fact]
    public void Non_resident_record_reports_real_size_runs_and_prefers_the_win32_name()
    {
        var bytes = Asset("mft", "nonresident.bin");

        MftRecordParser.TryParse(bytes, Sector, 101, out var record, out var error).Should().BeTrue(error);

        record!.DataSize.Should().Be(5_000_000_123);
        record.FileNames.Should().HaveCount(2);
        record.PrimaryName!.Name.Should().Be("big file.iso");
        record.DataRuns.Should().Equal(new DataRun(0, 0x40, 0x1000), new DataRun(0x40, 0x10, 0x1030), new DataRun(0x50, 1, null));
    }

    [Fact]
    public void Attribute_list_record_lists_extension_segments()
    {
        var bytes = Asset("mft", "attrlist.bin");

        MftRecordParser.TryParse(bytes, Sector, 102, out var record, out var error).Should().BeTrue(error);

        record!.IsDirectory.Should().BeTrue();
        record.AttributeList.Select(a => (a.Type, a.SegmentRecordNumber)).Should().Equal((0x30u, 102UL), (0x80u, 2047UL));
        record.DataSize.Should().BeNull("the data attribute lives in another segment");
    }

    [Fact]
    public void Torn_writes_and_bad_signatures_are_rejected()
    {
        var torn = Asset("mft", "resident.bin");
        torn[Sector - 1] ^= 0xFF;
        MftRecordParser.TryParse(torn, Sector, 100, out _, out var error).Should().BeFalse();
        error.Should().Contain("Torn write");

        var bad = Asset("mft", "resident.bin");
        bad[0] = (byte)'B';
        MftRecordParser.TryParse(bad, Sector, 100, out _, out error).Should().BeFalse();
        error.Should().Contain("signature");

        var random = new Random(11);
        var valid = Asset("mft", "nonresident.bin");
        for (var i = 0; i < 20_000; i++)
        {
            var copy = (byte[])valid.Clone();
            for (var k = random.Next(1, 6); k > 0; k--)
            {
                copy[random.Next(48, copy.Length)] = (byte)random.Next(256);
            }

            var act = () => MftRecordParser.TryParse(copy, Sector, 101, out _, out _);
            act.Should().NotThrow();
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x11, 0x05, 0x10, 0x00 }, "0:5@16")]
    [InlineData(new byte[] { 0x21, 0x02, 0x00, 0x01, 0x11, 0x03, 0xFE, 0x00 }, "0:2@256,2:3@254")]
    [InlineData(new byte[] { 0x01, 0x08, 0x11, 0x01, 0x05, 0x00 }, "0:8@sparse,8:1@5")]
    [InlineData(new byte[] { 0x00 }, "")]
    public void Decodes_data_runs_with_signed_relative_offsets(byte[] pairs, string expected)
    {
        MftRecordParser.TryDecodeRuns(pairs, 0, out var runs, out var error).Should().BeTrue(error);

        string.Join(',', runs.Select(r => $"{r.Vcn}:{r.ClusterCount}@{(r.Lcn?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "sparse")}")).Should().Be(expected);
    }

    // =============================================================================== fixture builders

    private static class Times
    {
        public const long Created = 133_000_000_000_000_000;
        public const long Modified = 133_100_000_000_000_000;
    }

    private static byte[] Buffer(long next, params byte[][] records)
    {
        var buffer = new byte[8 + records.Sum(r => r.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, next);
        var position = 8;
        foreach (var record in records)
        {
            record.CopyTo(buffer, position);
            position += record.Length;
        }

        return buffer;
    }

    private static byte[] V2(ulong frn, ulong parent, long usn, string name, uint attributes, UsnReason reason)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = Align8(60 + nameBytes.Length);
        var r = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(8), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(16), parent);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(24), usn);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(32), Times.Modified);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(40), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(58), 60);
        nameBytes.CopyTo(r, 60);
        return r;
    }

    private static byte[] V3(UInt128 frn, UInt128 parent, long usn, string name, uint attributes, UsnReason reason)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = Align8(76 + nameBytes.Length);
        var r = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 3);
        WriteUInt128(r.AsSpan(8), frn);
        WriteUInt128(r.AsSpan(24), parent);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(40), usn);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(48), Times.Modified);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(56), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(68), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(72), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(74), 76);
        nameBytes.CopyTo(r, 76);
        return r;
    }

    private static byte[] V4(UInt128 frn, UInt128 parent, long usn, UsnReason reason, (long Offset, long Length)[] extents)
    {
        var length = 64 + (extents.Length * 16);
        var r = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 4);
        WriteUInt128(r.AsSpan(8), frn);
        WriteUInt128(r.AsSpan(24), parent);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(40), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(48), (uint)reason);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(60), (ushort)extents.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(62), 16);
        for (var i = 0; i < extents.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(64 + (i * 16)), extents[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(72 + (i * 16)), extents[i].Length);
        }

        return r;
    }

    private static byte[] MftRecord(ulong recordNumber, ushort sequence, bool directory, (ulong Parent, string Name, byte Namespace)[] names,
        byte[]? residentData = null, (long Size, byte[] Runs)? nonResidentData = null, (uint Type, ulong Segment)[]? attributeList = null)
    {
        var r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        const int usaOffset = 48;
        const ushort usn = 0x0042;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), RecordSize / Sector + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(18), (ushort)names.Length);
        const int firstAttribute = 56;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(20), firstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(22), (ushort)(1 | (directory ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(44), (uint)recordNumber);

        var at = firstAttribute;
        var si = new byte[72];
        BinaryPrimitives.WriteInt64LittleEndian(si, Times.Created);
        BinaryPrimitives.WriteInt64LittleEndian(si.AsSpan(8), Times.Modified);
        BinaryPrimitives.WriteUInt32LittleEndian(si.AsSpan(32), directory ? 0x10u : 0x20u);
        BinaryPrimitives.WriteUInt32LittleEndian(si.AsSpan(52), 0x105);
        at = Resident(r, at, MftRecordParser.StandardInformation, si);
        if (attributeList is not null)
        {
            var list = new byte[attributeList.Length * 32];
            for (var i = 0; i < attributeList.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 32), attributeList[i].Type);
                BinaryPrimitives.WriteUInt16LittleEndian(list.AsSpan((i * 32) + 4), 32);
                list[(i * 32) + 7] = 26;
                BinaryPrimitives.WriteUInt64LittleEndian(list.AsSpan((i * 32) + 16), attributeList[i].Segment);
            }

            at = Resident(r, at, MftRecordParser.AttributeList, list);
        }

        foreach (var (parent, name, ns) in names)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var fn = new byte[66 + nameBytes.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(fn, parent);
            BinaryPrimitives.WriteInt64LittleEndian(fn.AsSpan(8), Times.Created);
            BinaryPrimitives.WriteInt64LittleEndian(fn.AsSpan(16), Times.Modified);
            fn[64] = (byte)name.Length;
            fn[65] = ns;
            nameBytes.CopyTo(fn, 66);
            at = Resident(r, at, MftRecordParser.FileName, fn);
        }

        if (residentData is not null)
        {
            at = Resident(r, at, MftRecordParser.Data, residentData);
        }

        if (nonResidentData is { } nr)
        {
            var length = Align8(64 + nr.Runs.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), MftRecordParser.Data);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 4), (uint)length);
            r[at + 8] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 24), 0x50);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(at + 32), 64);
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 40), Align8((int)Math.Min(nr.Size, int.MaxValue - 8)));
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 48), nr.Size);
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 56), nr.Size);
            nr.Runs.CopyTo(r, at + 64);
            at += length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), (uint)(at + 8));

        // Update sequence: save each sector's last two bytes in the array, stamp the sequence number.
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(usaOffset), usn);
        for (var s = 0; s < RecordSize / Sector; s++)
        {
            var tail = ((s + 1) * Sector) - 2;
            r.AsSpan(tail, 2).CopyTo(r.AsSpan(usaOffset + 2 + (s * 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(tail), usn);
        }

        return r;
    }

    private static int Resident(byte[] r, int at, uint type, byte[] value)
    {
        var length = Align8(24 + value.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), type);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 4), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(at + 20), 24);
        value.CopyTo(r, at + 24);
        return at + length;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static void WriteUInt128(Span<byte> span, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)value);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)(value >> 64));
    }
}
