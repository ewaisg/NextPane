using System.Buffers;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Ntfs;

/// <summary>Size and timestamps of one file from the MFT (§7.3 "raw $MFT size/timestamp fill").</summary>
public readonly record struct MftStat(ulong FileReference, ulong ParentReference, string? Name, uint Attributes, long Size, long CreatedUtc, long ModifiedUtc, bool IsDirectory, uint SecurityId);

/// <summary>
/// Reads the whole <c>$MFT</c> through a raw volume handle (P3.11): locates the MFT's own data runs from record 0,
/// reads it run by run in 1 MB blocks, applies fixups and parses every in-use record. Extension records (attribute
/// lists) contribute their unnamed <c>$DATA</c> size to the base record.
/// </summary>
public static class MftReader
{
    private const int BlockSize = 1 << 20;

    public static IReadOnlyDictionary<ulong, MftStat> ReadAll(NtfsVolume volume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var data = volume.GetVolumeData();
        var recordSize = data.BytesPerFileRecordSegment;
        var first = new byte[recordSize];
        volume.ReadAt(data.MftStartLcn * data.BytesPerCluster, first);
        if (!MftRecordParser.TryParse(first, data.BytesPerSector, 0, out var mft, out var error) || mft!.DataRuns.Count == 0)
        {
            throw new InvalidDataException($"Cannot read the $MFT record of {volume.Root}: {error ?? "no data runs"}.");
        }

        var result = new Dictionary<ulong, MftStat>();
        var extensionSizes = new Dictionary<ulong, long>();
        var totalRecords = data.MftValidDataLength / recordSize;
        long recordNumber = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            foreach (var run in mft.DataRuns)
            {
                if (run.Lcn is null)
                {
                    recordNumber += run.ClusterCount * data.BytesPerCluster / recordSize;
                    continue;
                }

                var runBytes = run.ClusterCount * data.BytesPerCluster;
                for (long done = 0; done < runBytes && recordNumber < totalRecords; done += BlockSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(BlockSize, runBytes - done);
                    var read = volume.ReadAt((run.Lcn.Value * data.BytesPerCluster) + done, buffer.AsSpan(0, length));
                    for (var offset = 0; offset + recordSize <= read && recordNumber < totalRecords; offset += recordSize, recordNumber++)
                    {
                        var span = buffer.AsSpan(offset, recordSize);
                        if (!MftRecordParser.TryParse(span, data.BytesPerSector, (ulong)recordNumber, out var record, out _) || !record!.InUse)
                        {
                            continue;
                        }

                        if (!record.IsBaseRecord)
                        {
                            if (record.DataSize is long extensionSize)
                            {
                                extensionSizes[record.BaseRecordReference & 0x0000FFFFFFFFFFFF] = extensionSize;
                            }

                            continue;
                        }

                        var name = record.PrimaryName;
                        result[record.FileReference] = new MftStat(record.FileReference, name?.ParentReference ?? 0, name?.Name,
                            record.StandardAttributes ?? 0, record.IsDirectory ? 0 : record.DataSize ?? -1, record.CreatedUtc ?? 0, record.ModifiedUtc ?? 0,
                            record.IsDirectory, record.SecurityId ?? 0);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Files whose $DATA lives in an extension record (attribute list).
        foreach (var (key, stat) in result.ToList())
        {
            if (stat.Size < 0)
            {
                result[key] = stat with { Size = extensionSizes.GetValueOrDefault(key & 0x0000FFFFFFFFFFFF, 0) };
            }
        }

        return result;
    }
}
