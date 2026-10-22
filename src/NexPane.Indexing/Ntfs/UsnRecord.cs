using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NexPane.Indexing.Ntfs;

/// <summary>USN change reasons (winioctl.h <c>USN_REASON_*</c>).</summary>
[Flags]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,
}

/// <summary>Range tracking extent of a V4 record.</summary>
public readonly record struct UsnExtent(long Offset, long Length);

/// <summary>
/// A parsed USN record (V2, V3 or V4). File references are 128-bit (V2's 64-bit references are zero-extended);
/// for NTFS the low 64 bits are the FRN (48-bit record number + 16-bit sequence number).
/// </summary>
public sealed record UsnRecord(
    int MajorVersion,
    int MinorVersion,
    UInt128 FileReference,
    UInt128 ParentFileReference,
    long Usn,
    long TimeStamp,
    UsnReason Reason,
    uint SourceInfo,
    uint SecurityId,
    uint FileAttributes,
    string? FileName,
    IReadOnlyList<UsnExtent> Extents,
    int RemainingExtents)
{
    public ulong FileReferenceLow => (ulong)FileReference;

    public ulong ParentReferenceLow => (ulong)ParentFileReference;

    public bool IsDirectory => (FileAttributes & 0x10) != 0;
}

/// <summary>
/// Parser for <c>USN_RECORD_V2</c>, <c>USN_RECORD_V3</c> and <c>USN_RECORD_V4</c> buffers returned by
/// <c>FSCTL_ENUM_USN_DATA</c> / <c>FSCTL_READ_USN_JOURNAL</c> (P3.09). Every length and offset is validated against the
/// record and buffer bounds; malformed input yields a parse error, never an exception.
/// </summary>
public static class UsnRecordParser
{
    public const int V2HeaderSize = 60;
    public const int V3HeaderSize = 76;
    public const int V4HeaderSize = 64;
    public const int MaxRecordLength = 64 * 1024;

    /// <summary>Parses one record at the start of <paramref name="buffer"/>. On success <paramref name="length"/> is the
    /// record length (the offset of the next record).</summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out UsnRecord? record, out int length, out string? error)
    {
        record = null;
        length = 0;
        error = null;
        if (buffer.Length < 8)
        {
            error = "Buffer shorter than a record header.";
            return false;
        }

        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]);
        var header = major switch
        {
            2 => V2HeaderSize,
            3 => V3HeaderSize,
            4 => V4HeaderSize,
            _ => -1,
        };
        if (header < 0)
        {
            error = $"Unsupported USN record version {major}.{minor}.";
            return false;
        }

        if (recordLength < header || recordLength > MaxRecordLength || recordLength > buffer.Length || recordLength % 8 != 0)
        {
            error = $"Invalid record length {recordLength} (header {header}, buffer {buffer.Length}).";
            return false;
        }

        var span = buffer[..(int)recordLength];
        switch (major)
        {
            case 2:
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
                var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[58..]);
                if (!TryName(span, nameOffset, nameLength, header, out var name, out error))
                {
                    return false;
                }

                record = new UsnRecord(major, minor,
                    BinaryPrimitives.ReadUInt64LittleEndian(span[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(span[16..]),
                    BinaryPrimitives.ReadInt64LittleEndian(span[24..]),
                    BinaryPrimitives.ReadInt64LittleEndian(span[32..]),
                    (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(span[40..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[44..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[48..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[52..]),
                    name, [], 0);
                break;
            }

            case 3:
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[72..]);
                var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[74..]);
                if (!TryName(span, nameOffset, nameLength, header, out var name, out error))
                {
                    return false;
                }

                record = new UsnRecord(major, minor,
                    ReadUInt128(span[8..]),
                    ReadUInt128(span[24..]),
                    BinaryPrimitives.ReadInt64LittleEndian(span[40..]),
                    BinaryPrimitives.ReadInt64LittleEndian(span[48..]),
                    (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(span[56..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[60..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[64..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[68..]),
                    name, [], 0);
                break;
            }

            default:
            {
                var remaining = BinaryPrimitives.ReadUInt32LittleEndian(span[56..]);
                var count = BinaryPrimitives.ReadUInt16LittleEndian(span[60..]);
                var extentSize = BinaryPrimitives.ReadUInt16LittleEndian(span[62..]);
                if (count > 0 && (extentSize < 16 || (long)count * extentSize > recordLength - header))
                {
                    error = $"Invalid V4 extents ({count} × {extentSize} bytes).";
                    return false;
                }

                var extents = new UsnExtent[count];
                for (var i = 0; i < count; i++)
                {
                    var at = span[(header + (i * extentSize))..];
                    extents[i] = new UsnExtent(BinaryPrimitives.ReadInt64LittleEndian(at), BinaryPrimitives.ReadInt64LittleEndian(at[8..]));
                }

                record = new UsnRecord(major, minor,
                    ReadUInt128(span[8..]),
                    ReadUInt128(span[24..]),
                    BinaryPrimitives.ReadInt64LittleEndian(span[40..]),
                    0,
                    (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(span[48..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[52..]), 0, 0, null, extents, (int)remaining);
                break;
            }
        }

        length = (int)recordLength;
        return true;
    }

    /// <summary>Parses a buffer of consecutive records starting at <paramref name="offset"/> (8 for enumeration and
    /// journal reads, whose first 8 bytes hold the next start USN / FRN). Stops at the first malformed record.</summary>
    public static ParsedBuffer ParseBuffer(ReadOnlySpan<byte> buffer, int offset = 8)
    {
        var records = new List<UsnRecord>();
        if (buffer.Length < 8 || offset < 0 || offset > buffer.Length)
        {
            return new ParsedBuffer(0, records, buffer.Length < 8 ? "Buffer shorter than the 8-byte continuation value." : "Offset outside the buffer.");
        }

        var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var position = offset;
        while (position < buffer.Length)
        {
            if (!TryParse(buffer[position..], out var record, out var length, out var error))
            {
                return new ParsedBuffer(next, records, $"Malformed record at offset {position}: {error}");
            }

            records.Add(record!);
            position += length;
        }

        return new ParsedBuffer(next, records, null);
    }

    private static bool TryName(ReadOnlySpan<byte> record, int offset, int length, int header, out string? name, out string? error)
    {
        name = null;
        error = null;
        if (length % 2 != 0 || offset < header || offset + length > record.Length)
        {
            error = $"File name (offset {offset}, length {length}) outside the record.";
            return false;
        }

        name = new string(MemoryMarshal.Cast<byte, char>(record.Slice(offset, length)));
        return true;
    }

    private static UInt128 ReadUInt128(ReadOnlySpan<byte> span) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(span[8..]), BinaryPrimitives.ReadUInt64LittleEndian(span));
}

/// <summary>Records of one IOCTL output buffer plus its continuation value (next FRN or next USN).</summary>
public sealed record ParsedBuffer(long Next, IReadOnlyList<UsnRecord> Records, string? Error);
