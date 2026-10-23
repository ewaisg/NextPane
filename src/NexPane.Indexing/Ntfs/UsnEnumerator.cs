using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Ntfs;

/// <summary>One enumerated entry; <paramref name="name"/> points into the IOCTL buffer and is valid only during the call.</summary>
public delegate void UsnEntryVisitor(ulong fileReference, ulong parentReference, uint attributes, ReadOnlySpan<char> name);

/// <summary>Where the time of an enumeration went (P3.38 diagnostics).</summary>
/// <param name="IoctlTime">Time spent inside <c>DeviceIoControl</c> (on the fetch thread).</param>
/// <param name="ParseTime">Time spent parsing records and in the visitor (on the calling thread).</param>
public readonly record struct UsnEnumerationStats(long Records, int Batches, TimeSpan IoctlTime, TimeSpan ParseTime, TimeSpan Elapsed);

/// <summary>
/// Whole-volume <c>FSCTL_ENUM_USN_DATA</c> enumeration (§7.3 "names + parent FRNs + attributes", §17 ≤ 5 s per 1M MFT
/// records). The initial fill uses the raw $MFT reader (§21 2026-10-23); this path is the fallback when the $MFT
/// cannot be read. 4 MB V2 batches; a fetch thread issues the next IOCTL (its start FRN is the first 8 bytes of the
/// previous output) while the caller parses the current batch in place, without per-record allocations.
/// </summary>
public static class UsnEnumerator
{
    public const int BufferSize = 4 << 20;
    private const int Buffers = 3;

    public static UsnEnumerationStats Enumerate(NtfsVolume volume, long highUsn, UsnEntryVisitor visitor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(visitor);
        var watch = Stopwatch.StartNew();
        using var free = new BlockingCollection<byte[]>(Buffers);
        using var filled = new BlockingCollection<(byte[] Buffer, int Length)>(Buffers);
        for (var i = 0; i < Buffers; i++)
        {
            free.Add(new byte[BufferSize], ct);
        }

        long ioctlTicks = 0;
        Exception? fetchError = null;
        var fetcher = new Thread(() =>
        {
            try
            {
                ulong next = 0;
                while (true)
                {
                    var buffer = free.Take(ct);
                    var start = Stopwatch.GetTimestamp();
                    var result = volume.EnumUsnData(next, highUsn, buffer, maxMajorVersion: 2);
                    ioctlTicks += Stopwatch.GetTimestamp() - start;
                    if (result.Error == NtfsVolume.ErrorHandleEof)
                    {
                        return;
                    }

                    if (!result.Succeeded)
                    {
                        throw new IOException($"FSCTL_ENUM_USN_DATA on {volume.Root} failed with Win32 error {result.Error}.", result.Error);
                    }

                    if (result.BytesReturned < 8)
                    {
                        throw new InvalidDataException($"FSCTL_ENUM_USN_DATA on {volume.Root} returned {result.BytesReturned} bytes.");
                    }

                    var following = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
                    filled.Add((buffer, result.BytesReturned), ct);
                    if (result.BytesReturned == 8 || following == next)
                    {
                        return;
                    }

                    next = following;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                fetchError = ex;
            }
            finally
            {
                filled.CompleteAdding();
            }
        }) { IsBackground = true, Name = "USN enumeration fetch" };
        fetcher.Start();

        long records = 0;
        var batches = 0;
        var parse = new Stopwatch();
        try
        {
            foreach (var (buffer, length) in filled.GetConsumingEnumerable(ct))
            {
                parse.Start();
                records += ParseBatch(buffer.AsSpan(0, length), visitor);
                parse.Stop();
                batches++;
                free.Add(buffer, ct);
            }
        }
        finally
        {
            fetcher.Join();
        }

        if (fetchError is not null)
        {
            throw fetchError is OperationCanceledException ? new OperationCanceledException(ct) : fetchError;
        }

        return new UsnEnumerationStats(records, batches, Stopwatch.GetElapsedTime(0, ioctlTicks), parse.Elapsed, watch.Elapsed);
    }

    /// <summary>Parses one <c>FSCTL_ENUM_USN_DATA</c> output (8-byte next FRN, then V2/V3 records) in place and returns the
    /// number of records. Lengths and offsets are validated; a malformed record throws <see cref="InvalidDataException"/>.</summary>
    public static int ParseBatch(ReadOnlySpan<byte> output, UsnEntryVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (output.Length < 8)
        {
            throw new InvalidDataException("Buffer shorter than the 8-byte continuation value.");
        }

        var count = 0;
        var position = 8;
        while (position + 8 <= output.Length)
        {
            var record = output[position..];
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record);
            var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var header = major == 2 ? UsnRecordParser.V2HeaderSize : major == 3 ? UsnRecordParser.V3HeaderSize : -1;
            if (header < 0 || length < header || length > record.Length || length > UsnRecordParser.MaxRecordLength || (length & 7) != 0)
            {
                throw new InvalidDataException($"Malformed USN record at offset {position} (version {major}, length {length}).");
            }

            ulong frn, parent;
            uint attributes;
            int nameLength, nameOffset;
            if (major == 2)
            {
                frn = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
                parent = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
                attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            }
            else
            {
                frn = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]); // low 64 bits of the 128-bit id (NTFS)
                parent = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
                attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[72..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[74..]);
            }

            if (nameOffset < header || (nameLength & 1) != 0 || nameOffset + nameLength > length)
            {
                throw new InvalidDataException($"USN record at offset {position} has its name outside the record.");
            }

            visitor(frn, parent, attributes, MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength)));
            count++;
            position += length;
        }

        return count;
    }
}
