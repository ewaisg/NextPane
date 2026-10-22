using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NexPane.Indexing.Ntfs;

/// <summary>One run of a non-resident attribute: <paramref name="Lcn"/> is null for sparse runs.</summary>
public readonly record struct DataRun(long Vcn, long ClusterCount, long? Lcn);

/// <summary><c>$FILE_NAME</c> attribute: parent reference, name and namespace (0 POSIX, 1 Win32, 2 DOS, 3 Win32+DOS).</summary>
public sealed record MftFileName(ulong ParentReference, string Name, byte Namespace, long CreatedUtc, long ModifiedUtc, long FileSize, uint Attributes)
{
    public ulong ParentRecordNumber => ParentReference & 0x0000FFFFFFFFFFFF;

    /// <summary>True for names visible to Win32 (not the 8.3 DOS alias).</summary>
    public bool IsWin32Name => Namespace != 2;
}

/// <summary><c>$ATTRIBUTE_LIST</c> entry: where an attribute of this file lives.</summary>
public sealed record MftAttributeListEntry(uint Type, string? Name, long LowestVcn, ulong SegmentReference)
{
    public ulong SegmentRecordNumber => SegmentReference & 0x0000FFFFFFFFFFFF;
}

/// <summary>Parsed FILE record (P3.11).</summary>
public sealed record MftFileRecord(
    ulong RecordNumber,
    ushort SequenceNumber,
    bool InUse,
    bool IsDirectory,
    ulong BaseRecordReference,
    long? CreatedUtc,
    long? ModifiedUtc,
    uint? StandardAttributes,
    uint? SecurityId,
    IReadOnlyList<MftFileName> FileNames,
    long? DataSize,
    IReadOnlyList<DataRun> DataRuns,
    IReadOnlyList<MftAttributeListEntry> AttributeList,
    IReadOnlyList<DataRun> AttributeListRuns)
{
    /// <summary>FRN: record number in the low 48 bits, sequence number in the high 16 bits.</summary>
    public ulong FileReference => RecordNumber | ((ulong)SequenceNumber << 48);

    public bool IsBaseRecord => (BaseRecordReference & 0x0000FFFFFFFFFFFF) == 0;

    /// <summary>The preferred display name (Win32 over DOS).</summary>
    public MftFileName? PrimaryName => FileNames.FirstOrDefault(n => n.Namespace is 1 or 3) ?? FileNames.FirstOrDefault(n => n.IsWin32Name) ?? (FileNames.Count > 0 ? FileNames[0] : null);
}

/// <summary>
/// Raw NTFS FILE record parser (P3.11): validates the "FILE" signature, applies the update-sequence fixups, and
/// decodes <c>$STANDARD_INFORMATION</c>, <c>$FILE_NAME</c>, the unnamed <c>$DATA</c> size (resident or
/// non-resident, with data runs) and <c>$ATTRIBUTE_LIST</c>. Malformed records are rejected, never thrown on.
/// </summary>
public static class MftRecordParser
{
    public const uint StandardInformation = 0x10;
    public const uint AttributeList = 0x20;
    public const uint FileName = 0x30;
    public const uint Data = 0x80;
    private const uint EndMarker = 0xFFFFFFFF;

    /// <summary>Applies fixups in place and parses. <paramref name="record"/> must be exactly one record.</summary>
    public static bool TryParse(Span<byte> record, int bytesPerSector, ulong recordNumber, out MftFileRecord? parsed, out string? error)
    {
        parsed = null;
        if (!TryApplyFixups(record, bytesPerSector, out error))
        {
            return false;
        }

        try
        {
            return TryParseAttributes(record, recordNumber, out parsed, out error);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Defensive: span slicing beyond validated bounds means the record is corrupt.
            error = "Corrupt record: " + ex.Message;
            parsed = null;
            return false;
        }
    }

    /// <summary>Verifies and replaces the last two bytes of every sector with the saved update-sequence values.</summary>
    public static bool TryApplyFixups(Span<byte> record, int bytesPerSector, out string? error)
    {
        error = null;
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8))
        {
            error = "Missing FILE signature.";
            return false;
        }

        if (bytesPerSector < 256 || record.Length % bytesPerSector != 0)
        {
            error = $"Record size {record.Length} is not a multiple of the sector size {bytesPerSector}.";
            return false;
        }

        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        var sectors = record.Length / bytesPerSector;
        if (usaCount != sectors + 1 || usaOffset < 40 || usaOffset + (usaCount * 2) > bytesPerSector - 2)
        {
            error = $"Invalid update sequence array (offset {usaOffset}, count {usaCount}, sectors {sectors}).";
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 0; i < sectors; i++)
        {
            var tail = record.Slice(((i + 1) * bytesPerSector) - 2, 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tail) != sequence)
            {
                error = $"Torn write: sector {i} does not end with the update sequence number.";
                return false;
            }

            record.Slice(usaOffset + 2 + (i * 2), 2).CopyTo(tail);
        }

        return true;
    }

    /// <summary>Decodes a mapping-pairs (data run) list.</summary>
    public static bool TryDecodeRuns(ReadOnlySpan<byte> pairs, long startVcn, out List<DataRun> runs, out string? error)
    {
        runs = [];
        error = null;
        var vcn = startVcn;
        long lcn = 0;
        var i = 0;
        while (i < pairs.Length && pairs[i] != 0)
        {
            var header = pairs[i++];
            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            if (lengthSize is 0 or > 8 || offsetSize > 8 || i + lengthSize + offsetSize > pairs.Length)
            {
                error = $"Invalid run header 0x{header:X2} at byte {i - 1}.";
                return false;
            }

            var length = ReadSigned(pairs.Slice(i, lengthSize));
            i += lengthSize;
            if (length <= 0)
            {
                error = "Run with non-positive length.";
                return false;
            }

            long? runLcn = null;
            if (offsetSize > 0)
            {
                lcn += ReadSigned(pairs.Slice(i, offsetSize));
                i += offsetSize;
                if (lcn < 0)
                {
                    error = "Run starts before cluster 0.";
                    return false;
                }

                runLcn = lcn;
            }

            runs.Add(new DataRun(vcn, length, runLcn));
            vcn += length;
        }

        return true;
    }

    private static bool TryParseAttributes(ReadOnlySpan<byte> record, ulong recordNumber, out MftFileRecord? parsed, out string? error)
    {
        parsed = null;
        error = null;
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        var bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
        var baseRecord = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        if (bytesInUse > record.Length || firstAttribute < 48 || firstAttribute >= bytesInUse)
        {
            error = $"Invalid record layout (first attribute {firstAttribute}, bytes in use {bytesInUse}).";
            return false;
        }

        long? created = null, modified = null, dataSize = null;
        uint? standardAttributes = null, securityId = null;
        var names = new List<MftFileName>();
        var dataRuns = new List<DataRun>();
        var attributeList = new List<MftAttributeListEntry>();
        var attributeListRuns = new List<DataRun>();
        var offset = (int)firstAttribute;
        while (offset + 16 <= bytesInUse)
        {
            var attribute = record[offset..(int)bytesInUse];
            var type = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
            if (type == EndMarker)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(attribute[4..]);
            if (length < 16 || length > attribute.Length || length % 8 != 0)
            {
                error = $"Attribute 0x{type:X} at {offset} has invalid length {length}.";
                return false;
            }

            attribute = attribute[..(int)length];
            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[10..]);
            if (nameLength > 0 && nameOffset + (nameLength * 2) > length)
            {
                error = $"Attribute 0x{type:X} name outside the attribute.";
                return false;
            }

            var attributeName = nameLength == 0 ? null : new string(MemoryMarshal.Cast<byte, char>(attribute.Slice(nameOffset, nameLength * 2)));
            ReadOnlySpan<byte> value = default;
            if (!nonResident)
            {
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
                if (valueOffset + (long)valueLength > length)
                {
                    error = $"Resident value of attribute 0x{type:X} outside the attribute.";
                    return false;
                }

                value = attribute.Slice(valueOffset, (int)valueLength);
            }
            else if (length < 64)
            {
                error = $"Non-resident attribute 0x{type:X} header too short.";
                return false;
            }

            switch (type)
            {
                case StandardInformation when !nonResident && value.Length >= 48:
                    created = BinaryPrimitives.ReadInt64LittleEndian(value);
                    modified = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
                    standardAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
                    if (value.Length >= 56)
                    {
                        securityId = BinaryPrimitives.ReadUInt32LittleEndian(value[52..]);
                    }

                    break;

                case FileName when !nonResident && value.Length >= 66:
                {
                    var chars = value[64];
                    if (66 + (chars * 2) > value.Length)
                    {
                        error = "$FILE_NAME name outside the value.";
                        return false;
                    }

                    names.Add(new MftFileName(
                        BinaryPrimitives.ReadUInt64LittleEndian(value),
                        new string(MemoryMarshal.Cast<byte, char>(value.Slice(66, chars * 2))),
                        value[65],
                        BinaryPrimitives.ReadInt64LittleEndian(value[8..]),
                        BinaryPrimitives.ReadInt64LittleEndian(value[16..]),
                        BinaryPrimitives.ReadInt64LittleEndian(value[48..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(value[56..])));
                    break;
                }

                case Data when attributeName is null:
                    if (nonResident)
                    {
                        var lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]);
                        if (lowestVcn == 0)
                        {
                            dataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
                        }

                        var pairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
                        if (pairsOffset >= length || !TryDecodeRuns(attribute[pairsOffset..], lowestVcn, out var runs, out error))
                        {
                            error ??= "Mapping pairs outside the attribute.";
                            return false;
                        }

                        dataRuns.AddRange(runs);
                    }
                    else
                    {
                        dataSize = value.Length;
                    }

                    break;

                case AttributeList:
                    if (nonResident)
                    {
                        var pairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
                        if (pairsOffset >= length || !TryDecodeRuns(attribute[pairsOffset..], 0, out var runs, out error))
                        {
                            error ??= "Attribute list mapping pairs outside the attribute.";
                            return false;
                        }

                        attributeListRuns.AddRange(runs);
                    }
                    else if (!TryParseAttributeList(value, attributeList, out error))
                    {
                        return false;
                    }

                    break;
            }

            offset += (int)length;
        }

        parsed = new MftFileRecord(recordNumber, sequence, (flags & 1) != 0, (flags & 2) != 0, baseRecord, created, modified, standardAttributes,
            securityId, names, dataSize, dataRuns, attributeList, attributeListRuns);
        return true;
    }

    /// <summary>Parses resident (or separately read non-resident) <c>$ATTRIBUTE_LIST</c> content.</summary>
    public static bool TryParseAttributeList(ReadOnlySpan<byte> value, List<MftAttributeListEntry> entries, out string? error)
    {
        error = null;
        var position = 0;
        while (position + 26 <= value.Length)
        {
            var entry = value[position..];
            var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            var nameLength = entry[6];
            var nameOffset = entry[7];
            if (recordLength < 26 || recordLength > entry.Length || (nameLength > 0 && nameOffset + (nameLength * 2) > recordLength))
            {
                error = $"Invalid attribute list entry at {position}.";
                return false;
            }

            entries.Add(new MftAttributeListEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(entry),
                nameLength == 0 ? null : new string(MemoryMarshal.Cast<byte, char>(entry.Slice(nameOffset, nameLength * 2))),
                BinaryPrimitives.ReadInt64LittleEndian(entry[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[16..])));
            position += recordLength;
        }

        return true;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        var shift = 64 - (bytes.Length * 8);
        return shift == 0 ? value : (value << shift) >> shift; // sign-extend
    }
}
